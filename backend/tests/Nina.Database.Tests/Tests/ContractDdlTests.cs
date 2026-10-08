using System.Text.RegularExpressions;
using Nina.Database.Tests.Infrastructure;

namespace Nina.Database.Tests.Tests;

/// <summary>
/// Anexo B 10 (SR-021): contrato (<c>contracts/openapi.yaml</c> v1.0.1, somente leitura) x DDL. Todo valor de enum que o contrato pode emitir
/// precisa ser aceito pelo CHECK da coluna correspondente, e os limites de texto/faixas do contrato cabem no banco. As diferenças
/// intencionais (derivadas na API) estão listadas aqui, com o motivo.
/// </summary>
[Collection(PgClusterGroup.Name)]
public sealed partial class ContractDdlTests(PgCluster cluster) : DbTestBase(cluster)
{
    private static readonly Lazy<string> Contract = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "contracts", "openapi.yaml")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir?.FullName ?? throw new FileNotFoundException("contracts/openapi.yaml"), "contracts", "openapi.yaml"));
    });

    [GeneratedRegex(@"enum:\s*\[([^\]]*)\]")]
    private static partial Regex EnumRegex();

    private static List<string> EnumOf(string schema, string? property = null)
    {
        var text = Contract.Value;
        var start = text.IndexOf($"\n    {schema}:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"schema {schema} nao encontrado no contrato");
        var next = Regex.Match(text[(start + 5)..], @"\n    [A-Za-z]+:\n");
        var block = next.Success ? text.Substring(start, next.Index + 5) : text[start..];
        if (property is not null)
        {
            var at = block.IndexOf($"{property}:", StringComparison.Ordinal);
            Assert.True(at >= 0, $"{schema}.{property} nao encontrado");
            block = block[at..];
        }

        var m = EnumRegex().Match(block);
        Assert.True(m.Success, $"enum de {schema}.{property} nao encontrado");
        return [.. m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(v => v != "null")];
    }

    private async Task AssertAcceptedAsync(string table, string column, IEnumerable<string> contractValues, params string[] intentionallyDerived)
    {
        var defs = await OwnerScalarAsync<string>(
            $"""
            SELECT string_agg(pg_get_constraintdef(c.oid), ' ') FROM pg_constraint c
             WHERE c.conrelid = 'nina.{table}'::regclass AND c.contype = 'c' AND pg_get_constraintdef(c.oid) LIKE '%{column}%'
            """) ?? string.Empty;
        var rejected = contractValues.Where(v => !intentionallyDerived.Contains(v) && !defs.Contains($"'{v}'", StringComparison.Ordinal)).ToArray();
        Assert.True(rejected.Length == 0, $"{table}.{column} nao aceita do contrato: {string.Join(", ", rejected)}");
    }

    [Fact]
    public async Task Enums_emitted_by_the_contract_are_accepted_by_the_database()
    {
        await AssertAcceptedAsync("baby", "sex", EnumOf("BabyCreate", "sex"));
        await AssertAcceptedAsync("caregiver_membership", "role", EnumOf("Role"));
        await AssertAcceptedAsync("caregiver_membership", "role", EnumOf("InvitableRole"));
        await AssertAcceptedAsync("user_identity", "provider", EnumOf("IdentityProvider"));
        await AssertAcceptedAsync("diaper_event", "diaper_type", EnumOf("DiaperType"));
        await AssertAcceptedAsync("feeding_session", "feeding_type", EnumOf("FeedingType"));
        await AssertAcceptedAsync("feeding_session", "milk_type", EnumOf("MilkType"));
        await AssertAcceptedAsync("feeding_session", "side", EnumOf("BreastSide"));
        await AssertAcceptedAsync("wake_event", "source", EnumOf("WakeEvent", "source"));
        await AssertAcceptedAsync("privacy_request", "request_type", EnumOf("PrivacyRequestType"));
        await AssertAcceptedAsync("privacy_request", "status", EnumOf("PrivacyRequest", "status"));
        await AssertAcceptedAsync("notification_preference", "category", EnumOf("NotificationCategory"));
        await AssertAcceptedAsync("auth_session", "platform", EnumOf("Session", "platform"));
        await AssertAcceptedAsync("device_push_token", "platform", EnumOf("PushTokenRegistration", "platform"));
        await AssertAcceptedAsync("device_push_token", "environment", EnumOf("PushTokenRegistration", "environment"));
        await AssertAcceptedAsync("data_export_request", "status", EnumOf("DataExport", "status"));
    }

    [Fact]
    public async Task Membership_and_deletion_states_differ_from_the_contract_only_where_the_api_derives_them()
    {
        // EXPIRED e derivado (convite PENDING com invite_expires_at no passado); REQUESTED do pedido de exclusao e o estado "recem-criado"
        // que a API devolve antes do agendamento. O banco nao tem esses estados de proposito.
        await AssertAcceptedAsync("caregiver_membership", "status", EnumOf("Membership", "status"), "EXPIRED");
        await AssertAcceptedAsync("account_deletion_request", "status", EnumOf("AccountDeletion", "status"), "REQUESTED");
        // BLOCKED existe so no banco (politica BLOCK): o contrato nao o lista; a API o traduz (ver docs: decisao pendente)
        Assert.DoesNotContain("BLOCKED", EnumOf("AccountDeletion", "status"));
    }

    [Fact]
    public async Task Consent_purpose_keys_map_by_case_to_the_seeded_catalog()
    {
        var keys = await OwnerScalarAsync<string>("SELECT string_agg(purpose_key, ',') FROM nina.consent_purpose") ?? string.Empty;
        foreach (var key in EnumOf("PurposeKey"))
        {
            Assert.Contains(key.ToLowerInvariant(), keys.Split(','));           // contrato MAIUSCULO, banco minusculo (mapeamento no Identity)
        }
    }

    [Fact]
    public async Task Text_limits_and_ranges_of_the_contract_fit_the_database()
    {
        var methodLimits = Regex.Matches(Contract.Value, @"method_or_place:[^\n]*maxLength: (\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToArray();
        Assert.Single(methodLimits);
        var notesLimits = Regex.Matches(Contract.Value, @"\n\s+notes:[^\n]*maxLength: (\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToArray();
        Assert.Single(notesLimits);

        var dbMethod = await OwnerScalarAsync<int>("SELECT substring(pg_get_constraintdef(oid) from 'length\\(method_or_place\\) <= (\\d+)')::int FROM pg_constraint WHERE conrelid = 'nina.sleep_session'::regclass AND pg_get_constraintdef(oid) LIKE '%method_or_place%'");
        Assert.Equal(methodLimits[0], dbMethod);
        foreach (var table in new[] { "sleep_session", "feeding_session", "pumping_session", "diaper_event" })
        {
            var dbNotes = await OwnerScalarAsync<int>($"SELECT substring(pg_get_constraintdef(oid) from 'length\\(notes\\) <= (\\d+)')::int FROM pg_constraint WHERE conrelid = 'nina.{table}'::regclass AND pg_get_constraintdef(oid) LIKE '%notes%'");
            Assert.Equal(notesLimits[0], dbNotes);
        }

        // janela de arrependimento: contrato 1..30 (SR-021) = validacao do banco
        Assert.Equal("23514", await FailsAsync(Role.Config, W.Alice, "UPDATE nina.app_parameter SET value = '0'::jsonb WHERE param_key = 'privacy.deletion_grace_days'"));
        Assert.Equal("23514", await FailsAsync(Role.Config, W.Alice, "UPDATE nina.app_parameter SET value = '31'::jsonb WHERE param_key = 'privacy.deletion_grace_days'"));
        Assert.Null(await FailsAsync(Role.Config, W.Alice, "UPDATE nina.app_parameter SET value = '30'::jsonb WHERE param_key = 'privacy.deletion_grace_days'"));
    }

    [Fact]
    public async Task The_database_stores_volumes_as_numeric_so_the_contracts_integer_ml_always_fits()
    {
        Assert.Equal("numeric", await OwnerScalarAsync<string>("SELECT data_type FROM information_schema.columns WHERE table_schema = 'nina' AND table_name = 'feeding_session' AND column_name = 'volume_ml'"));
        Assert.Null(await FailsAsync(Role.App, W.Erin, $"INSERT INTO nina.feeding_session (id, baby_id, start_at, tz, feeding_type, volume_ml) VALUES (gen_random_uuid(), '{W.BabyA}', now(), 'UTC', 'BOTTLE', 5000)"));
        Assert.Equal("23514", await FailsAsync(Role.App, W.Erin, $"INSERT INTO nina.feeding_session (id, baby_id, start_at, tz, feeding_type, volume_ml) VALUES (gen_random_uuid(), '{W.BabyA}', now(), 'UTC', 'BOTTLE', 5001)"));
    }
}
