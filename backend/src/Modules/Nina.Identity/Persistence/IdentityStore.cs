using Nina.SharedKernel.Data;
using Nina.SharedKernel.Security;
using Npgsql;

namespace Nina.Identity.Persistence;

public sealed record UserRow(
    Guid Id,
    string Email,
    DateTimeOffset? EmailVerifiedAt,
    string? Locale,
    string? Timezone,
    string Status,
    DateTimeOffset CreatedAt,
    string? PasswordHash,
    string? DisplayName = null);

public sealed record SessionRow(
    Guid Id,
    Guid UserId,
    Guid DeviceId,
    string? DeviceLabel,
    string Platform,
    string? AppVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset AbsoluteExpiresAt,
    DateTimeOffset? RevokedAt);

public sealed record RefreshTokenRow(Guid Id, Guid SessionId, DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt);

public sealed record IdentityRow(string Provider, string Subject, DateTimeOffset LinkedAt, Guid UserId);

public sealed record PurposeRow(string PurposeKey, string CurrentVersion, bool Required, string Scope, bool InMvp);

public sealed record ConsentRow(
    Guid Id, string PurposeKey, string PolicyVersion, string Status, DateTimeOffset RecordedAt, Guid? BabyId, string Source, long Seq);

/// <summary>Consultas e comandos SQL do Identity. Cada método roda dentro de uma transação já aberta (e com contexto de RLS quando preciso).</summary>
public static class IdentityStore
{
    // Leitura do PRÓPRIO usuário (RLS por nina.user_id: SR-003). A busca por e-mail, sem sessão, usa a função auth_lookup_user_by_email.
    private const string UserSelect =
        """
        SELECT u.id, u.email, u.email_verified_at, u.locale, u.timezone, u.status, u.created_at, c.password_hash, u.display_name
          FROM nina.app_user u LEFT JOIN nina.user_credential c ON c.user_id = u.id
        """;

    private const string SessionSelect =
        """
        SELECT id, user_id, device_id, device_label, platform, app_version, created_at, last_seen_at, absolute_expires_at, revoked_at
          FROM nina.auth_session
        """;

    // ---------------------------------------------------------------- usuários

    public static Task<UserRow?> FindUserByEmailAsync(DbTx tx, string normalizedEmail) =>
        tx.QueryFirstAsync(
            "SELECT id, email, email_verified_at, locale, timezone, status, created_at, password_hash, display_name FROM nina.auth_lookup_user_by_email(@e)",
            ReadUser, Db.Text("e", normalizedEmail));

    public static Task<UserRow?> FindUserByIdAsync(DbTx tx, Guid id) =>
        tx.QueryFirstAsync(UserSelect + " WHERE u.id = @id", ReadUser, Db.Uuid("id", id));

    /// <summary>
    /// Exige <c>tx.SetUserAsync(id)</c> antes: a RLS só deixa o usuário inserir a própria linha, sempre NÃO verificada
    /// (NR-09: o app não grava <c>email_verified_at</c>; só <see cref="VerifyEmailCodeAsync"/> e <see cref="RegisterSocialUserAsync"/> o definem).
    /// </summary>
    public static Task InsertUserAsync(
        DbTx tx, Guid id, string email, string locale, string? timezone, DateTimeOffset now, string? displayName = null) =>
        tx.ExecAsync(
            """
            INSERT INTO nina.app_user (id, email, display_name, locale, timezone, created_at, updated_at)
            VALUES (@id, @email, @display, @locale, @tz, @now, @now)
            """,
            Db.Uuid("id", id), Db.Text("email", email), Db.Text("display", displayName),
            Db.Text("locale", locale), Db.Text("tz", timezone), Db.Timestamp("now", now));

    /// <summary>Cadastro social: conta já verificada (o provedor atestou o e-mail) + identidade, por função definer com MAC do servidor (NR-09).</summary>
    public static Task RegisterSocialUserAsync(
        DbTx tx, ServerMac mac, Guid id, string email, string? displayName, string locale, string? timezone,
        string provider, string subject, DateTimeOffset now)
    {
        var at = ServerSignatures.Floor(now);
        return tx.ExecAsync(
            "SELECT nina.register_social_user(@id, @email, @display, @locale, @tz, @p, @s, @now, @mac)",
            Db.Uuid("id", id), Db.Text("email", email), Db.Text("display", displayName), Db.Text("locale", locale), Db.Text("tz", timezone),
            Db.Text("p", provider), Db.Text("s", subject), Db.Timestamp("now", at),
            Db.Bytes("mac", mac.Sign(ServerSignatures.SocialRegister, ServerSignatures.SocialRegisterMessage(id, provider, subject, email))));
    }

    public static Task UpdateProfileAsync(DbTx tx, Guid userId, string? displayName, string? locale, string? timezone) =>
        tx.ExecAsync(
            """
            UPDATE nina.app_user
               SET display_name = COALESCE(NULLIF(btrim(@display), ''), display_name),
                   locale = COALESCE(@locale, locale),
                   timezone = COALESCE(@tz::nina.iana_tz, timezone)
             WHERE id = @id
            """,
            Db.Uuid("id", userId), Db.Text("display", displayName), Db.Text("locale", locale), Db.Text("tz", timezone));

    public static Task UpsertCredentialAsync(DbTx tx, Guid userId, string hash, DateTimeOffset now) =>
        tx.ExecAsync(
            """
            INSERT INTO nina.user_credential (user_id, password_hash, hash_algorithm, changed_at)
            VALUES (@u, @hash, 'ARGON2ID', @now)
            ON CONFLICT (user_id) DO UPDATE SET password_hash = EXCLUDED.password_hash,
              hash_algorithm = EXCLUDED.hash_algorithm, changed_at = EXCLUDED.changed_at
            """,
            Db.Uuid("u", userId), Db.Text("hash", hash), Db.Timestamp("now", now));

    // ------------------------------------------------------------- identidades

    public static Task<List<IdentityRow>> ListIdentitiesAsync(DbTx tx, Guid userId) =>
        tx.QueryAsync(
            "SELECT provider, provider_subject, linked_at, user_id FROM nina.user_identity WHERE user_id = @u ORDER BY linked_at",
            r => new IdentityRow(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetGuid(3)),
            Db.Uuid("u", userId));

    public static Task<IdentityRow?> FindIdentityAsync(DbTx tx, string provider, string subject) =>
        tx.QueryFirstAsync(
            "SELECT provider, provider_subject, linked_at, user_id FROM nina.auth_lookup_identity(@p, @s)",
            r => new IdentityRow(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetGuid(3)),
            Db.Text("p", provider), Db.Text("s", subject));

    public static Task InsertIdentityAsync(DbTx tx, Guid userId, string provider, string subject, string? email, DateTimeOffset now) =>
        tx.ExecAsync(
            """
            INSERT INTO nina.user_identity (user_id, provider, provider_subject, email_at_link, linked_at)
            VALUES (@u, @p, @s, @e, @now)
            """,
            Db.Uuid("u", userId), Db.Text("p", provider), Db.Text("s", subject), Db.Text("e", email), Db.Timestamp("now", now));

    // ----------------------------------------------------- recuperação / códigos

    public static Task InsertRecoveryAsync(DbTx tx, Guid userId, byte[] hash, DateTimeOffset now, DateTimeOffset expires) =>
        tx.ExecAsync(
            "INSERT INTO nina.recovery_request (user_id, token_hash, created_at, expires_at) VALUES (@u, @h, @now, @exp)",
            Db.Uuid("u", userId), Db.Bytes("h", hash), Db.Timestamp("now", now), Db.Timestamp("exp", expires));

    // ------------------------------------------------- código de verificação do e-mail (NR-09)

    /// <summary>
    /// Emite o código de verificação do e-mail do usuário do contexto (invalida o anterior) por <c>nina.email_code_issue</c>, assinado
    /// pelo servidor. Devolve false (sem emitir) se a conta já está verificada/inativa ou se o último código é mais novo que
    /// <paramref name="minInterval"/> (intervalo de reenvio). Exige <c>tx.SetUserAsync</c>.
    /// </summary>
    public static async Task<bool> IssueEmailCodeAsync(
        DbTx tx, ServerMac mac, Guid userId, byte[] codeHash, DateTimeOffset now, DateTimeOffset expires, TimeSpan minInterval)
    {
        var at = ServerSignatures.Floor(now);
        var until = ServerSignatures.Floor(expires);
        var id = await tx.ScalarAsync<Guid?>(
            "SELECT nina.email_code_issue(@h, @now, @exp, @interval, @mac)",
            Db.Bytes("h", codeHash), Db.Timestamp("now", at), Db.Timestamp("exp", until),
            Db.P("interval", minInterval, NpgsqlTypes.NpgsqlDbType.Interval),
            Db.Bytes("mac", mac.Sign(ServerSignatures.EmailCode, ServerSignatures.EmailCodeMessage(userId, codeHash, until))));
        return id is not null;
    }

    /// <summary>
    /// Confere o código por <c>nina.email_code_verify</c> (contador de tentativas persistente e atômico; esgotado, o código é
    /// invalidado) e, se confere, marca o e-mail como verificado. Devolve o usuário ou null (sempre sem exceção).
    /// </summary>
    public static Task<Guid?> VerifyEmailCodeAsync(DbTx tx, string email, byte[] codeHash, DateTimeOffset now) =>
        tx.ScalarAsync<Guid?>(
            "SELECT nina.email_code_verify(@e, @h, @now)", Db.Text("e", email), Db.Bytes("h", codeHash), Db.Timestamp("now", now));

    /// <summary>Consome (marca como usado) o código/token se ainda for válido (função definer: a busca é pelo hash, sem sessão). Devolve o usuário dono ou null.</summary>
    public static Task<Guid?> ConsumeRecoveryAsync(DbTx tx, byte[] hash, DateTimeOffset now) =>
        tx.ScalarAsync<Guid?>(
            "SELECT nina.auth_consume_recovery(@h, @now)",
            Db.Bytes("h", hash), Db.Timestamp("now", now));

    /// <summary>
    /// Emite no livro-razão <c>nina.reauth_jti</c> o <c>jti</c> (hash) do token de reautenticação que a API acabou de assinar, com
    /// usuário, sessão, escopos e validade, por <c>nina.reauth_issue</c> e MAC do servidor (NR-03: só vale jti emitido pelo servidor).
    /// Exige <c>tx.SetUserAsync</c>.
    /// </summary>
    public static Task IssueReauthAsync(
        DbTx tx, ServerMac mac, Guid userId, byte[] jtiHash, Guid sessionId, IReadOnlyCollection<string> scopes,
        DateTimeOffset issued, DateTimeOffset expires)
    {
        var from = ServerSignatures.Floor(issued);
        var until = ServerSignatures.Floor(expires);
        return tx.ExecAsync(
            "SELECT nina.reauth_issue(@h, @s, @scopes, @iat, @exp, @mac)",
            Db.Bytes("h", jtiHash), Db.Uuid("s", sessionId),
            Db.P("scopes", scopes.ToArray(), NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text),
            Db.Timestamp("iat", from), Db.Timestamp("exp", until),
            Db.Bytes("mac", mac.Sign(ServerSignatures.ReauthIssue, ServerSignatures.ReauthIssueMessage(userId, sessionId, jtiHash, scopes, from, until))));
    }

    /// <summary>
    /// Consome (uso único atômico entre instâncias) o <c>jti</c> de reautenticação já EMITIDO para este usuário e esta sessão, no
    /// escopo pedido; false se foi forjado, já consumido, expirado ou de outro escopo/sessão. Exige <c>tx.SetUserAsync</c>.
    /// </summary>
    public static async Task<bool> TryConsumeReauthJtiAsync(DbTx tx, byte[] hash, string scope, Guid sessionId) =>
        await tx.ScalarAsync<bool>(
            "SELECT nina.consume_reauth_jti(@h, @scope, @s)", Db.Bytes("h", hash), Db.Text("scope", scope), Db.Uuid("s", sessionId));

    public static Task InvalidateOpenRecoveryAsync(DbTx tx, Guid userId, DateTimeOffset now) =>
        tx.ExecAsync(
            "UPDATE nina.recovery_request SET used_at = @now WHERE user_id = @u AND used_at IS NULL",
            Db.Uuid("u", userId), Db.Timestamp("now", now));

    // ----------------------------------------------------------------- sessões

    public static Task RevokeDeviceSessionsAsync(DbTx tx, Guid userId, Guid deviceId, DateTimeOffset now) =>
        tx.ExecAsync(
            """
            UPDATE nina.auth_session SET revoked_at = @now, revoked_reason = 'LOGOUT'
             WHERE user_id = @u AND device_id = @d AND revoked_at IS NULL
            """,
            Db.Uuid("u", userId), Db.Uuid("d", deviceId), Db.Timestamp("now", now));

    public static async Task<bool> DeviceHasAnySessionAsync(DbTx tx, Guid userId, Guid deviceId) =>
        await tx.ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM nina.auth_session WHERE user_id = @u AND device_id = @d)",
            Db.Uuid("u", userId), Db.Uuid("d", deviceId));

    public static Task InsertSessionAsync(
        DbTx tx, Guid id, Guid userId, Guid deviceId, string? label, string platform, string? appVersion, DateTimeOffset now, DateTimeOffset absoluteExpires) =>
        tx.ExecAsync(
            """
            INSERT INTO nina.auth_session
              (id, user_id, device_id, device_label, platform, app_version, created_at, last_seen_at, absolute_expires_at)
            VALUES (@id, @u, @d, @label, @platform, @app, @now, @now, @abs)
            """,
            Db.Uuid("id", id), Db.Uuid("u", userId), Db.Uuid("d", deviceId), Db.Text("label", label),
            Db.Text("platform", platform), Db.Text("app", appVersion), Db.Timestamp("now", now), Db.Timestamp("abs", absoluteExpires));

    public static Task<SessionRow?> GetSessionAsync(DbTx tx, Guid sessionId, bool forUpdate = false) =>
        tx.QueryFirstAsync(
            SessionSelect + " WHERE id = @id" + (forUpdate ? " FOR UPDATE" : string.Empty), ReadSession, Db.Uuid("id", sessionId));

    public static Task<List<SessionRow>> ListActiveSessionsAsync(DbTx tx, Guid userId, DateTimeOffset now) =>
        tx.QueryAsync(
            SessionSelect + " WHERE user_id = @u AND revoked_at IS NULL AND absolute_expires_at > @now ORDER BY last_seen_at DESC",
            ReadSession, Db.Uuid("u", userId), Db.Timestamp("now", now));

    public static Task RevokeSessionAsync(DbTx tx, Guid sessionId, string reason, DateTimeOffset now) =>
        tx.ExecAsync(
            "UPDATE nina.auth_session SET revoked_at = @now, revoked_reason = @reason WHERE id = @id AND revoked_at IS NULL",
            Db.Uuid("id", sessionId), Db.Text("reason", reason), Db.Timestamp("now", now));

    /// <summary>Revoga as sessões ativas do usuário exceto <paramref name="exceptSession"/>; devolve os dispositivos afetados.</summary>
    public static Task<List<Guid>> RevokeAllSessionsAsync(DbTx tx, Guid userId, Guid? exceptSession, string reason, DateTimeOffset now) =>
        tx.QueryAsync(
            """
            UPDATE nina.auth_session SET revoked_at = @now, revoked_reason = @reason
             WHERE user_id = @u AND revoked_at IS NULL AND (@except::uuid IS NULL OR id <> @except::uuid)
            RETURNING device_id
            """,
            r => r.GetGuid(0), Db.Uuid("u", userId), Db.Uuid("except", exceptSession), Db.Text("reason", reason), Db.Timestamp("now", now));

    public static Task TouchSessionAsync(DbTx tx, Guid sessionId, DateTimeOffset now) =>
        tx.ExecAsync("UPDATE nina.auth_session SET last_seen_at = @now WHERE id = @id", Db.Uuid("id", sessionId), Db.Timestamp("now", now));

    public static async Task<bool> IsSessionActiveAsync(DbTx tx, Guid sessionId, DateTimeOffset now) =>
        await tx.ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM nina.auth_session WHERE id = @id AND revoked_at IS NULL AND absolute_expires_at > @now)",
            Db.Uuid("id", sessionId), Db.Timestamp("now", now));

    // ----------------------------------------------------------- refresh tokens

    public static Task InsertRefreshTokenAsync(DbTx tx, Guid sessionId, byte[] hash, DateTimeOffset now, DateTimeOffset expires) =>
        tx.ExecAsync(
            "INSERT INTO nina.refresh_token (session_id, token_hash, issued_at, expires_at) VALUES (@s, @h, @now, @exp)",
            Db.Uuid("s", sessionId), Db.Bytes("h", hash), Db.Timestamp("now", now), Db.Timestamp("exp", expires));

    public static Task<RefreshTokenRow?> GetRefreshTokenForUpdateAsync(DbTx tx, byte[] hash) =>
        tx.QueryFirstAsync(
            "SELECT id, session_id, expires_at, used_at FROM nina.refresh_token WHERE token_hash = @h FOR UPDATE",
            r => new RefreshTokenRow(r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3)),
            Db.Bytes("h", hash));

    public static Task MarkRefreshTokenUsedAsync(DbTx tx, Guid id, DateTimeOffset now) =>
        tx.ExecAsync("UPDATE nina.refresh_token SET used_at = @now WHERE id = @id", Db.Uuid("id", id), Db.Timestamp("now", now));

    // ------------------------------------------------------------- push tokens

    public static async Task<bool> HasActiveSessionForDeviceAsync(DbTx tx, Guid userId, Guid deviceId, DateTimeOffset now) =>
        await tx.ScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM nina.auth_session
                            WHERE user_id = @u AND device_id = @d AND revoked_at IS NULL AND absolute_expires_at > @now)
            """,
            Db.Uuid("u", userId), Db.Uuid("d", deviceId), Db.Timestamp("now", now));

    /// <summary>
    /// Função definer <c>register_push_token</c>: idempotente por (usuário, dispositivo, plataforma) e trata a troca de dono do aparelho
    /// (token de usuário sem sessão ativa no dispositivo é reatribuído; com sessão ativa, 23505 = conflito). O usuário é o do contexto.
    /// </summary>
    public static async Task<(DateTimeOffset Created, DateTimeOffset Updated)?> UpsertPushTokenAsync(
        DbTx tx, Guid deviceId, string platform, string token, string? environment, string? locale, string? appVersion,
        bool? osNotificationsAuthorized, DateTimeOffset now)
    {
        var row = await tx.QueryFirstAsync(
            "SELECT created_at, last_seen_at FROM nina.register_push_token(@d, @p, @t, @env, @loc, @app, @os, @now)",
            r => Tuple.Create(r.GetFieldValue<DateTimeOffset>(0), r.GetFieldValue<DateTimeOffset>(1)),
            Db.Uuid("d", deviceId), Db.Text("p", platform), Db.Text("t", token), Db.Text("env", environment), Db.Text("loc", locale),
            Db.Text("app", appVersion), Db.P("os", osNotificationsAuthorized, NpgsqlTypes.NpgsqlDbType.Boolean), Db.Timestamp("now", now));
        return row is null ? null : (row.Item1, row.Item2);
    }

    public static Task DeletePushTokensAsync(DbTx tx, Guid userId, IReadOnlyCollection<Guid> deviceIds) =>
        tx.ExecAsync(
            "DELETE FROM nina.device_push_token WHERE user_id = @u AND device_id = ANY(@d)",
            Db.Uuid("u", userId), Db.P("d", deviceIds.ToArray(), NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid));

    // ----------------------------------------------------------- consentimentos

    public static Task<List<PurposeRow>> ListPurposesAsync(DbTx tx) =>
        tx.QueryAsync(
            """
            SELECT purpose_key, current_version, is_required, scope, in_mvp
              FROM nina.consent_purpose WHERE current_version IS NOT NULL ORDER BY purpose_key
            """,
            r => new PurposeRow(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetString(3), r.GetBoolean(4)));

    public static Task InsertConsentAsync(
        DbTx tx, Guid userId, Guid? babyId, string purposeKey, string version, string textHash, string locale,
        string status, string source, string? appVersion, string platform, DateTimeOffset now) =>
        tx.ExecAsync(
            """
            INSERT INTO nina.consent_record
              (user_id, subject_baby_id, purpose_key, policy_version, text_hash, locale, status, recorded_at, source, app_version, platform)
            VALUES (@u, @baby, @purpose, @version, @hash, @locale, @status, @now, @source, @app, @platform)
            """,
            Db.Uuid("u", userId), Db.Uuid("baby", babyId), Db.Text("purpose", purposeKey), Db.Text("version", version),
            Db.Text("hash", textHash), Db.Text("locale", locale), Db.Text("status", status), Db.Timestamp("now", now),
            Db.Text("source", source), Db.Text("app", appVersion), Db.Text("platform", platform));

    public static Task<List<ConsentRow>> ListConsentsAsync(DbTx tx, Guid userId) =>
        tx.QueryAsync(
            """
            SELECT consent_id, purpose_key, policy_version, status, recorded_at, subject_baby_id, source, seq
              FROM nina.consent_record WHERE user_id = @u ORDER BY seq DESC
            """,
            r => new ConsentRow(
                r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4),
                r.IsDBNull(5) ? null : r.GetGuid(5), r.GetString(6), r.GetInt64(7)),
            Db.Uuid("u", userId));

    public static async Task<bool> CanReadBabyAsync(DbTx tx, Guid babyId) =>
        await tx.ScalarAsync<bool>("SELECT nina.can_read_baby(@b)", Db.Uuid("b", babyId));

    // ------------------------------------------------------------------- mapeamento

    private static UserRow ReadUser(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5),
        r.GetFieldValue<DateTimeOffset>(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8));

    private static SessionRow ReadSession(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.GetGuid(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.GetFieldValue<DateTimeOffset>(6),
        r.GetFieldValue<DateTimeOffset>(7),
        r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9));
}
