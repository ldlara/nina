using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Nina.Family.Persistence;
using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;
using Npgsql;

namespace Nina.Family.Services;

/// <summary>Papéis do contrato.</summary>
internal static class Roles
{
    public const string Owner = "OWNER";
    public const string Caregiver = "CAREGIVER";
    public const string ReadOnly = "READ_ONLY";
}

/// <summary>
/// Autorização por bebê no servidor (INV-12, RB-007). Sem vínculo ativo: <c>404</c> uniforme (inclusive bebê inexistente ou excluído);
/// vínculo encerrado do próprio usuário: <c>403 ACCESS_REVOKED</c>; vínculo ativo sem papel suficiente: <c>403 FORBIDDEN_ROLE</c>.
/// </summary>
internal static class Guard
{
    public static async Task<string> RequireAsync(DbTx tx, Guid userId, Guid babyId, bool ownerOnly)
    {
        var access = await FamilyStore.GetAccessAsync(tx, userId, babyId);
        if (access.Role is null)
        {
            throw access.Revoked ? AccessRevoked() : ProblemException.NotFound();
        }

        if (ownerOnly && access.Role != Roles.Owner)
        {
            throw ForbiddenRole();
        }

        return access.Role;
    }

    public static ProblemException AccessRevoked() => ProblemException.Forbidden("ACCESS_REVOKED", "Access revoked");

    public static ProblemException ForbiddenRole() => ProblemException.Forbidden("FORBIDDEN_ROLE", "Role not allowed");
}

/// <summary>Tradução de erros de negócio do banco (SQLSTATE <c>NN*</c> da migração 0001) para RFC 7807, sem repassar o texto do banco.</summary>
internal static class FamilyErrors
{
    public static ProblemException? Map(PostgresException ex) => ex.SqlState switch
    {
        "NN007" => ProblemException.Conflict("OWNER_DECISION_REQUIRED", "Decide the destination of shared babies"),
        "NN014" => ProblemException.Unauthorized("REAUTH_REQUIRED", "Reauthentication required"),
        "NN057" => new ProblemException(StatusCodes.Status429TooManyRequests, "QUOTA_EXCEEDED", "Daily quota exceeded") { RetryAfterSeconds = 3600 },
        "NN058" => ProblemException.Conflict("OWNER_MUST_TRANSFER", "Transfer the ownership first"),
        "NN059" or "NN015" or PostgresErrorCodes.InsufficientPrivilege => ProblemException.NotFound(),
        "NN051" => ProblemException.Conflict("MEMBERSHIP_STATE_CONFLICT", "The membership is not in a state that allows this operation"),
        "NN010" => ProblemException.Conflict("OWNER_REQUIRED", "A baby must always have an active Owner"),
        PostgresErrorCodes.UniqueViolation when ex.ConstraintName == "baby_pkey" =>
            ProblemException.Validation(new FieldError("id", "ENTITY_ID_UNAVAILABLE")),
        PostgresErrorCodes.UniqueViolation when ex.ConstraintName?.StartsWith("membership_", StringComparison.Ordinal) == true =>
            ProblemException.Conflict("ALREADY_MEMBER", "Already a member"),
        _ => null,
    };
}

/// <summary>Executa o trabalho numa transação com contexto de RLS e converte erros de negócio do banco em <see cref="ProblemException"/>.</summary>
public sealed class FamilyDb(NinaDb db)
{
    public async Task<T> RunAsync<T>(Guid userId, Func<DbTx, Task<T>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await db.InTransactionAsync(userId, work, cancellationToken);
        }
        catch (PostgresException ex) when (FamilyErrors.Map(ex) is { } problem)
        {
            throw problem;
        }
    }

    public Task RunAsync(Guid userId, Func<DbTx, Task> work, CancellationToken cancellationToken) =>
        RunAsync<bool>(userId, async tx =>
        {
            await work(tx);
            return true;
        }, cancellationToken);
}

/// <summary>Limites de taxa do módulo (SEC-040): sempre por usuário (e IP nas operações com token de convite); excesso gera <c>429</c> + <c>Retry-After</c>.</summary>
public sealed class FamilyRateGate(IRateLimiter limiter, IOptions<FamilyOptions> options, IRequestContext request)
{
    private FamilyRateLimits Limits => options.Value.RateLimits;

    public void General(Guid userId) =>
        Enforce($"family:any:{userId:N}", Limits.RequestsPerUserPerMinute, TimeSpan.FromMinutes(1));

    public void BabyCreate(Guid userId) =>
        Enforce($"family:baby-create:{userId:N}", Limits.BabyCreatePerUserPerHour, TimeSpan.FromHours(1));

    public void Invite(Guid userId) =>
        Enforce($"family:invite:{userId:N}", Limits.InvitePerUserPerHour, TimeSpan.FromHours(1));

    public void Resend(Guid userId) =>
        Enforce($"family:resend:{userId:N}", Limits.ResendPerUserPerHour, TimeSpan.FromHours(1));

    public void Sensitive(Guid userId) =>
        Enforce($"family:sensitive:{userId:N}", Limits.SensitivePerUserPerHour, TimeSpan.FromHours(1));

    public void InvitationToken(Guid userId)
    {
        Enforce($"family:invite-token:u:{userId:N}", Limits.InvitationTokenOpsPerUserPerMinute, TimeSpan.FromMinutes(1));
        if (request.ClientIp is { } ip)
        {
            Enforce($"family:invite-token:ip:{ip}", Limits.InvitationTokenOpsPerIpPerMinute, TimeSpan.FromMinutes(1));
        }
    }

    private void Enforce(string key, int limit, TimeSpan window)
    {
        var decision = limiter.Consume(key, limit, window);
        if (!decision.Allowed)
        {
            throw ProblemException.RateLimited(Math.Max(1, decision.RetryAfterSeconds));
        }
    }
}

/// <summary>
/// <c>Idempotency-Key</c> (AD-10): guarda por 24 h a resposta de sucesso por (usuário, operação, chave). Mesmo corpo: repete a resposta com
/// <c>Idempotent-Replayed: true</c>; corpo diferente: <c>422 IDEMPOTENCY_KEY_REUSE</c>. Execuções com a mesma chave são serializadas.
/// LIMITAÇÃO: memória por instância (o banco não tem tabela de idempotência); com várias réplicas use armazenamento compartilhado.
/// </summary>
public sealed class IdempotencyStore(TimeProvider time, IOptions<FamilyOptions> options, IOptions<JsonOptions> json)
{
    private const int MaxEntries = 50_000;
    private const int Stripes = 64;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, Stripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private sealed record Entry(string Fingerprint, int Status, string Body, DateTimeOffset At);

    public static string Fingerprint(string scope, params object?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "|" + JsonSerializer.Serialize(parts, NinaJson.Options))));

    public async Task<IResult> ExecuteAsync(
        HttpContext context, Guid userId, string operation, string fingerprint, Func<Task<(int Status, object Body)>> action)
    {
        var raw = context.Request.Headers["Idempotency-Key"].ToString();
        if (raw.Length == 0)
        {
            var (status, body) = await action();
            return Results.Json(body, json.Value.SerializerOptions, statusCode: status);
        }

        if (!Guid.TryParse(raw, out var keyGuid))
        {
            throw ProblemException.Validation(new FieldError("Idempotency-Key", "INVALID_FORMAT"));
        }

        var key = $"{userId:N}:{operation}:{keyGuid:N}";
        var gate = _locks[(uint)StringComparer.Ordinal.GetHashCode(key) % Stripes];
        await gate.WaitAsync(context.RequestAborted);
        try
        {
            var now = time.GetUtcNow();
            if (_entries.TryGetValue(key, out var existing) && now - existing.At < Retention)
            {
                if (existing.Fingerprint != fingerprint)
                {
                    throw new ProblemException(StatusCodes.Status422UnprocessableEntity, "IDEMPOTENCY_KEY_REUSE", "Idempotency key reused with a different payload");
                }

                context.Response.Headers["Idempotent-Replayed"] = "true";
                return Results.Text(existing.Body, "application/json", Encoding.UTF8, existing.Status);
            }

            var (status, body) = await action();
            var text = JsonSerializer.Serialize(body, json.Value.SerializerOptions);
            Store(key, new Entry(fingerprint, status, text, now), now);
            context.Response.Headers["Idempotent-Replayed"] = "false";
            return Results.Text(text, "application/json", Encoding.UTF8, status);
        }
        finally
        {
            gate.Release();
        }
    }

    private TimeSpan Retention => TimeSpan.FromHours(Math.Max(1, options.Value.IdempotencyRetentionHours));

    private void Store(string key, Entry entry, DateTimeOffset now)
    {
        if (_entries.Count >= MaxEntries)
        {
            foreach (var (k, v) in _entries)
            {
                if (now - v.At >= Retention)
                {
                    _entries.TryRemove(k, out _);
                }
            }

            if (_entries.Count >= MaxEntries)
            {
                foreach (var k in _entries.OrderBy(e => e.Value.At).Take(MaxEntries / 10).Select(e => e.Key).ToList())
                {
                    _entries.TryRemove(k, out _);
                }
            }
        }

        _entries[key] = entry;
    }
}

/// <summary>Validação de campos com códigos estáveis (ADR-0010 item 9).</summary>
internal sealed class FamilyValidation
{
    private readonly List<FieldError> _errors = [];

    public void Add(FieldError error) => _errors.Add(error);

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
        {
            throw ProblemException.Validation(_errors);
        }
    }

    public string? DisplayName(string field, string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _errors.Add(new FieldError(field, "REQUIRED"));
            return null;
        }

        if (name.Length > 60)
        {
            _errors.Add(new FieldError(field, "TOO_LONG"));
            return null;
        }

        if (name.Any(char.IsControl))
        {
            _errors.Add(new FieldError(field, "INVALID_FORMAT"));
            return null;
        }

        return name;
    }

    public string? Sex(string field, string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is not ("FEMALE" or "MALE" or "OTHER" or "UNSPECIFIED"))
        {
            _errors.Add(new FieldError(field, "UNSUPPORTED_VALUE"));
            return null;
        }

        return value;
    }

    public string? Timezone(string field, string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length is 0 or > 64 || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+){0,2}$"))
        {
            _errors.Add(new FieldError(field, "INVALID_FORMAT"));
            return null;
        }

        return value;
    }

    public string? InvitableRole(string field, string? value)
    {
        if (value is null)
        {
            _errors.Add(new FieldError(field, "REQUIRED"));
            return null;
        }

        if (value is not (Roles.Caregiver or Roles.ReadOnly))
        {
            _errors.Add(new FieldError(field, "UNSUPPORTED_VALUE"));
            return null;
        }

        return value;
    }

    public string? Email(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _errors.Add(new FieldError(field, "REQUIRED"));
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 254 || !System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$"))
        {
            _errors.Add(new FieldError(field, "INVALID_FORMAT"));
            return null;
        }

        return normalized;
    }

    public string? Token(string field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _errors.Add(new FieldError(field, "REQUIRED"));
            return null;
        }

        if (value.Length > 512)
        {
            _errors.Add(new FieldError(field, "TOO_LONG"));
            return null;
        }

        if (value.Length < 16)
        {
            _errors.Add(new FieldError(field, "TOO_SHORT"));
            return null;
        }

        return value;
    }
}
