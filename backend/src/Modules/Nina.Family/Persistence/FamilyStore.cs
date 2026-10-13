using Nina.SharedKernel.Data;
using Npgsql;
using NpgsqlTypes;

namespace Nina.Family.Persistence;

/// <summary>Papel ativo do usuário no bebê (<c>null</c> = sem vínculo ativo) e se já teve vínculo revogado (<c>ACCESS_REVOKED</c>).</summary>
public sealed record Access(string? Role, bool Revoked);

public sealed record BabyRow(
    Guid Id,
    string DisplayName,
    DateOnly BirthDate,
    DateOnly? DueDate,
    string? Sex,
    string Timezone,
    string? PhotoRef,
    string? MyRole,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateOnly AsOf,
    int ChronologicalDays,
    int? CorrectedDays,
    bool CorrectionApplied);

public sealed record MembershipRow(
    Guid Id,
    Guid BabyId,
    Guid? UserId,
    string? InvitedEmail,
    string Role,
    string Status,
    bool Expired,
    DateTimeOffset InvitedAt,
    DateTimeOffset? InviteExpiresAt,
    DateTimeOffset? AcceptedAt);

public sealed record MemberRef(Guid MembershipId, Guid UserId, string? DisplayName, string Role);

public sealed record SelfProfile(string? DisplayName, string? Timezone, string? Locale, string? EmailNormalized);

public sealed record InvitationPreviewRow(string Role, string? InviterDisplayName, string? BabyInitial, DateTimeOffset ExpiresAt);

public sealed record AcceptResult(string Result, Guid? BabyId, Guid? MembershipId);

public sealed record RequiredConsent(string PurposeKey, string Version);

/// <summary>
/// SQL do módulo Family. O app (papel <c>nina_app</c>) lê por RLS e escreve vínculos só pelas funções definer da migração 0001
/// (secção 10): nada aqui contorna a RLS. Cada método roda numa transação já aberta com <c>nina.user_id</c> definido.
/// </summary>
public static class FamilyStore
{
    private const string BabySelect =
        """
        SELECT b.id, b.display_name, b.birth_date, b.due_date, b.sex, b.timezone::text, b.photo_ref, nina.baby_role(b.id),
               b.version, b.created_at, b.updated_at, t.today, a.chronological_days, a.corrected_days, a.correction_applied
          FROM nina.baby b
         CROSS JOIN LATERAL (SELECT (now() AT TIME ZONE b.timezone)::date AS today) t
         CROSS JOIN LATERAL nina.age_calculation(b.birth_date, b.due_date, t.today) a
        """;

    private const string MembershipSelect =
        """
        SELECT m.id, m.baby_id, m.user_id, m.invited_email, m.role, m.status,
               (m.status = 'PENDING' AND m.invite_expires_at IS NOT NULL AND m.invite_expires_at <= now()) AS expired,
               m.invited_at, m.invite_expires_at, m.accepted_at
          FROM nina.caregiver_membership m
        """;

    // ---------------------------------------------------------------- acesso

    public static async Task<Access> GetAccessAsync(DbTx tx, Guid userId, Guid babyId)
    {
        var row = await tx.QueryFirstAsync(
            """
            SELECT nina.baby_role(@b),
                   EXISTS (SELECT 1 FROM nina.caregiver_membership m WHERE m.baby_id = @b AND m.user_id = @u AND m.status = 'REVOKED')
            """,
            r => new Access(r.IsDBNull(0) ? null : r.GetString(0), r.GetBoolean(1)),
            Db.Uuid("b", babyId), Db.Uuid("u", userId));
        return row ?? new Access(null, false);
    }

    public static Task SetDeviceAsync(DbTx tx, Guid? deviceId) =>
        deviceId is null
            ? Task.CompletedTask
            : tx.ExecAsync("SELECT set_config('nina.device_id', @d, true)", Db.Text("d", deviceId.Value.ToString("D")));

    // ---------------------------------------------------------------- usuário e consentimento

    public static Task<SelfProfile?> GetSelfAsync(DbTx tx, Guid userId) =>
        tx.QueryFirstAsync(
            "SELECT display_name, timezone::text, locale, email_normalized FROM nina.app_user WHERE id = @u",
            r => new SelfProfile(
                r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)),
            Db.Uuid("u", userId));

    /// <summary>Declaração de responsável legal vigente: genérica (sem bebê) ou para este bebê, na versão corrente do documento.</summary>
    public static async Task<bool> HasGuardianConsentAsync(DbTx tx, Guid userId, Guid babyId) =>
        await tx.ScalarAsync<bool>(
            """
            SELECT EXISTS (
              SELECT 1 FROM nina.consent_current c JOIN nina.consent_purpose p ON p.purpose_key = c.purpose_key
               WHERE c.user_id = @u AND c.purpose_key = 'child_data_guardian' AND c.status = 'GRANTED'
                 AND c.policy_version = p.current_version AND (c.subject_baby_id IS NULL OR c.subject_baby_id = @b))
            """,
            Db.Uuid("u", userId), Db.Uuid("b", babyId));

    public static Task<List<RequiredConsent>> ListPendingRequiredUserConsentsAsync(DbTx tx, Guid userId) =>
        tx.QueryAsync(
            """
            SELECT p.purpose_key, p.current_version FROM nina.consent_purpose p
             WHERE p.is_required AND p.scope = 'USER' AND p.current_version IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM nina.consent_current c
                                WHERE c.user_id = @u AND c.purpose_key = p.purpose_key AND c.subject_baby_id IS NULL
                                  AND c.status = 'GRANTED' AND c.policy_version = p.current_version)
             ORDER BY p.purpose_key
            """,
            r => new RequiredConsent(r.GetString(0), r.GetString(1)),
            Db.Uuid("u", userId));

    public static Task<string?> GetPurposeVersionAsync(DbTx tx, string purposeKey) =>
        tx.ScalarAsync<string>("SELECT current_version FROM nina.consent_purpose WHERE purpose_key = @k", Db.Text("k", purposeKey));

    public static Task<(string? Platform, string? AppVersion)> GetSessionClientAsync(DbTx tx, Guid? sessionId) =>
        sessionId is null
            ? Task.FromResult<(string?, string?)>((null, null))
            : GetSessionClientCoreAsync(tx, sessionId.Value);

    private static async Task<(string? Platform, string? AppVersion)> GetSessionClientCoreAsync(DbTx tx, Guid sessionId)
    {
        var row = await tx.QueryFirstAsync(
            "SELECT platform, app_version FROM nina.auth_session WHERE id = @s",
            r => Tuple.Create(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)),
            Db.Uuid("s", sessionId));
        return (row?.Item1, row?.Item2);
    }

    // ---------------------------------------------------------------- bebê

    public static Task<bool> IsKnownTimezoneAsync(DbTx tx, string timezone) =>
        tx.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_timezone_names WHERE name = @tz)", Db.Text("tz", timezone));

    public static async Task<DateOnly> LocalDateAsync(DbTx tx, string timezone) =>
        await tx.ScalarAsync<object>("SELECT (now() AT TIME ZONE @tz)::date", Db.Text("tz", timezone)) switch
        {
            DateOnly date => date,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => throw new InvalidOperationException("data local indisponível"),
        };

    public static Task<BabyRow?> GetBabyAsync(DbTx tx, Guid babyId) =>
        tx.QueryFirstAsync(BabySelect + " WHERE b.id = @id AND b.deleted_at IS NULL", ReadBaby, Db.Uuid("id", babyId));

    public static Task<List<BabyRow>> ListBabiesAsync(DbTx tx) =>
        tx.QueryAsync(
            BabySelect + " WHERE b.deleted_at IS NULL AND b.id IN (SELECT nina.readable_babies()) ORDER BY b.created_at, b.id",
            ReadBaby);

    public static async Task<Guid> EnsureFamilyAsync(DbTx tx, Guid userId)
    {
        await tx.ExecAsync(
            "INSERT INTO nina.family (owner_user_id) VALUES (@u) ON CONFLICT (owner_user_id) DO NOTHING", Db.Uuid("u", userId));
        return await tx.ScalarAsync<Guid>("SELECT id FROM nina.family WHERE owner_user_id = @u", Db.Uuid("u", userId));
    }

    public static async Task InsertBabyWithOwnerAsync(
        DbTx tx, Guid userId, Guid babyId, Guid familyId, string name, DateOnly birth, DateOnly? due, string? sex, string timezone)
    {
        await tx.ExecAsync(
            """
            INSERT INTO nina.baby (id, family_id, display_name, birth_date, due_date, sex, timezone)
            VALUES (@id, @family, @name, @birth, @due, @sex, @tz)
            """,
            Db.Uuid("id", babyId), Db.Uuid("family", familyId), Db.Text("name", name),
            Db.P("birth", birth, NpgsqlDbType.Date), Db.P("due", due, NpgsqlDbType.Date), Db.Text("sex", sex), Db.Text("tz", timezone));
        await tx.ExecAsync(
            """
            INSERT INTO nina.caregiver_membership (baby_id, user_id, role, status, accepted_at)
            VALUES (@id, @u, 'OWNER', 'ACTIVE', now())
            """,
            Db.Uuid("id", babyId), Db.Uuid("u", userId));
    }

    /// <summary>UPDATE condicionado à versão (quando informada). As colunas vêm de lista fixa; valores sempre parametrizados.</summary>
    public static async Task<int> UpdateBabyAsync(
        DbTx tx, Guid babyId, IReadOnlyDictionary<string, (object? Value, NpgsqlDbType Type)> changes, long? expectedVersion)
    {
        var sets = new List<string>();
        var parameters = new List<NpgsqlParameter> { Db.Uuid("id", babyId), Db.P("v", expectedVersion, NpgsqlDbType.Bigint) };
        var i = 0;
        foreach (var (column, (value, type)) in changes)
        {
            var name = "p" + i++;
            sets.Add($"{column} = @{name}");
            parameters.Add(Db.P(name, value, type));
        }

        return await tx.ExecAsync(
            $"UPDATE nina.baby SET {string.Join(", ", sets)} WHERE id = @id AND deleted_at IS NULL AND (@v::bigint IS NULL OR version = @v)",
            [.. parameters]);
    }

    public static async Task<int> CountOtherActiveMembersAsync(DbTx tx, Guid babyId, Guid userId) =>
        (int)await tx.ScalarAsync<long>(
            "SELECT count(*) FROM nina.caregiver_membership WHERE baby_id = @b AND status = 'ACTIVE' AND user_id <> @u",
            Db.Uuid("b", babyId), Db.Uuid("u", userId));

    public static Task DeleteBabyAsync(DbTx tx, Guid babyId, byte[] reauthJtiHash, bool acknowledgeOthers) =>
        tx.ExecAsync(
            "SELECT nina.delete_baby(@b, @h, @ack)", Db.Uuid("b", babyId), Db.Bytes("h", reauthJtiHash),
            Db.P("ack", acknowledgeOthers, NpgsqlDbType.Boolean));

    public static Task TransferOwnershipAsync(DbTx tx, Guid babyId, Guid newOwnerMembershipId, byte[] reauthJtiHash) =>
        tx.ExecAsync(
            "SELECT nina.transfer_ownership(@b, @m, @h)", Db.Uuid("b", babyId), Db.Uuid("m", newOwnerMembershipId), Db.Bytes("h", reauthJtiHash));

    // ---------------------------------------------------------------- vínculos

    /// <summary>Um vínculo do bebê visível ao usuário (Owner vê todos; os demais só o próprio). Sempre filtrado por <c>baby_id</c>.</summary>
    public static Task<MembershipRow?> FindMembershipAsync(DbTx tx, Guid babyId, Guid membershipId) =>
        tx.QueryFirstAsync(
            MembershipSelect + " WHERE m.baby_id = @b AND m.id = @m", ReadMembership, Db.Uuid("b", babyId), Db.Uuid("m", membershipId));

    public static Task<Guid?> GetOwnActiveMembershipIdAsync(DbTx tx, Guid babyId, Guid userId) =>
        tx.ScalarAsync<Guid?>(
            "SELECT id FROM nina.caregiver_membership WHERE baby_id = @b AND user_id = @u AND status = 'ACTIVE'",
            Db.Uuid("b", babyId), Db.Uuid("u", userId));

    /// <summary>Vínculos <c>PENDING</c> e <c>ACTIVE</c> do bebê, na visão do usuário (RLS: Owner todos, demais só o próprio).</summary>
    public static Task<List<MembershipRow>> ListMembershipsAsync(DbTx tx, Guid babyId) =>
        tx.QueryAsync(
            MembershipSelect + " WHERE m.baby_id = @b AND m.status IN ('PENDING', 'ACTIVE') ORDER BY m.invited_at, m.id",
            ReadMembership, Db.Uuid("b", babyId));

    /// <summary>Membros ativos com nome exibido (única via para ver o <c>display_name</c> de outro usuário).</summary>
    public static Task<List<MemberRef>> ListMemberRefsAsync(DbTx tx, Guid babyId) =>
        tx.QueryAsync(
            "SELECT membership_id, user_id, display_name, role FROM nina.baby_member_refs(@b)",
            r => new MemberRef(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)),
            Db.Uuid("b", babyId));

    public static Task<Guid?> LookupUserIdByEmailAsync(DbTx tx, string normalizedEmail) =>
        tx.ScalarAsync<Guid?>("SELECT id FROM nina.auth_lookup_user_by_email(@e)", Db.Text("e", normalizedEmail));

    public static Task<bool> HasLiveLinkAsync(DbTx tx, Guid babyId, Guid userId) =>
        tx.ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM nina.caregiver_membership WHERE baby_id = @b AND user_id = @u AND status IN ('PENDING', 'ACTIVE'))",
            Db.Uuid("b", babyId), Db.Uuid("u", userId));

    public static Task<bool> HasPendingInviteForEmailAsync(DbTx tx, Guid babyId, string normalizedEmail) =>
        tx.ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM nina.caregiver_membership WHERE baby_id = @b AND status = 'PENDING' AND invited_email = @e)",
            Db.Uuid("b", babyId), Db.Text("e", normalizedEmail));

    public static Task<Guid> InsertInvitationAsync(
        DbTx tx, Guid babyId, Guid inviterId, string email, string role, byte[] tokenHash, int ttlDays) =>
        tx.ScalarAsync<Guid>(
            """
            INSERT INTO nina.caregiver_membership (baby_id, invited_email, role, status, invited_by, invite_token_hash, invite_expires_at)
            VALUES (@b, @e, @r, 'PENDING', @u, @h, now() + make_interval(days => @d))
            RETURNING id
            """,
            Db.Uuid("b", babyId), Db.Text("e", email), Db.Text("r", role), Db.Uuid("u", inviterId), Db.Bytes("h", tokenHash),
            Db.P("d", ttlDays, NpgsqlDbType.Integer));

    public static Task RemoveMemberAsync(DbTx tx, Guid membershipId) =>
        tx.ExecAsync("SELECT nina.remove_member(@m)", Db.Uuid("m", membershipId));

    public static Task LeaveBabyAsync(DbTx tx, Guid babyId) =>
        tx.ExecAsync("SELECT nina.leave_baby(@b)", Db.Uuid("b", babyId));

    public static Task SetMemberRoleAsync(DbTx tx, Guid membershipId, string role) =>
        tx.ExecAsync("SELECT nina.set_member_role(@m, @r)", Db.Uuid("m", membershipId), Db.Text("r", role));

    // ---------------------------------------------------------------- convites (lado do convidado)

    public static Task<InvitationPreviewRow?> InspectInvitationAsync(DbTx tx, byte[] tokenHash) =>
        tx.QueryFirstAsync(
            "SELECT role, inviter_display_name, baby_initial, invite_expires_at FROM nina.inspect_invitation(@h)",
            r => new InvitationPreviewRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)),
            Db.Bytes("h", tokenHash));

    public static Task<AcceptResult?> AcceptInvitationAsync(
        DbTx tx, byte[] tokenHash, string policyVersion, string textHash, string locale, string platform, string? appVersion) =>
        tx.QueryFirstAsync(
            "SELECT result, baby_id, membership_id FROM nina.accept_invitation(@h, @v, @t, @l, @p, @a)",
            r => new AcceptResult(r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2)),
            Db.Bytes("h", tokenHash), Db.Text("v", policyVersion), Db.Text("t", textHash), Db.Text("l", locale), Db.Text("p", platform),
            Db.Text("a", appVersion));

    public static Task<string?> DeclineInvitationAsync(DbTx tx, byte[] tokenHash) =>
        tx.ScalarAsync<string>("SELECT nina.decline_invitation(@h)", Db.Bytes("h", tokenHash));

    // ---------------------------------------------------------------- leitores

    private static BabyRow ReadBaby(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.GetFieldValue<DateOnly>(2),
        r.IsDBNull(3) ? null : r.GetFieldValue<DateOnly>(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.GetInt64(8),
        r.GetFieldValue<DateTimeOffset>(9),
        r.GetFieldValue<DateTimeOffset>(10),
        r.GetFieldValue<DateOnly>(11),
        r.GetInt32(12),
        r.IsDBNull(13) ? null : r.GetInt32(13),
        r.GetBoolean(14));

    private static MembershipRow ReadMembership(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.IsDBNull(2) ? null : r.GetGuid(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4),
        r.GetString(5),
        r.GetBoolean(6),
        r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9));
}
