using Nina.Database.Tests.Infrastructure;
using Npgsql;

namespace Nina.Database.Tests.Tests;

/// <summary>SR-002/SR-004/SR-009 (CRÍTICO/ALTO): vínculos só mudam por funções; sem autopromoção, reativação, migração entre bebês nem tomada de tenant.</summary>
[Collection(PgClusterGroup.Name)]
public sealed class MembershipTests(PgCluster cluster) : DbTestBase(cluster)
{
    private const string Arm = "SELECT nina.guard_arm('membership'); ";

    private async Task<(Guid Id, byte[] Hash)> InviteAsync(Guid owner, Guid baby, string? email, string role = "CAREGIVER", Guid? user = null)
    {
        var id = Guid.NewGuid();
        var hash = Hash("token-" + id);
        await using var s = await AsApp(owner);
        await s.ExecAsync(
            """
            INSERT INTO nina.caregiver_membership (id, baby_id, user_id, invited_email, role, status, invited_by, invite_token_hash, invite_expires_at)
            VALUES (@id, @baby, @user, @email, @role, 'PENDING', @owner, @hash, now() + interval '7 days')
            """,
            P("id", id), P("baby", baby), P("user", user), P("email", email), P("role", role), P("owner", owner), Bytes("hash", hash));
        await s.CommitAsync();
        return (id, hash);
    }

    private async Task<string> AcceptAsync(Guid user, byte[] hash, string version = "1.0.0")
    {
        await using var s = await AsApp(user);
        var result = await s.ScalarAsync<string>(
            "SELECT result FROM nina.accept_invitation(@h, @v, @t, 'pt-BR', 'IOS', '1.0.0')",
            Bytes("h", hash), P("v", version), P("t", new string('a', 64)));
        await s.CommitAsync();
        return result!;
    }

    private async Task<Guid> NewUserAsync(string email, bool verified = true)
    {
        var id = Guid.NewGuid();
        await OwnerAsync($"INSERT INTO nina.app_user (id, email, email_verified_at, display_name) VALUES ('{id}', '{email}', {(verified ? "now()" : "NULL")}, 'Novo')");
        return id;
    }

    // ---------------------------------------------------------------- SR-002

    [Fact]
    public async Task T1_E1_a_member_cannot_promote_themselves_or_change_any_membership_column()
    {
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, $"UPDATE nina.caregiver_membership SET role = 'CAREGIVER' WHERE user_id = '{W.Carol}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, $"UPDATE nina.caregiver_membership SET role = 'OWNER' WHERE user_id = '{W.Carol}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, $"UPDATE nina.caregiver_membership SET status = 'ACTIVE' WHERE user_id = '{W.Carol}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, $"UPDATE nina.caregiver_membership SET role = 'READ_ONLY' WHERE id = '{W.MembershipErin}'"));   // nem o Owner por UPDATE direto
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, $"DELETE FROM nina.caregiver_membership WHERE user_id = '{W.Carol}'"));
        Assert.Equal("READ_ONLY", await OwnerScalarAsync<string>($"SELECT role FROM nina.caregiver_membership WHERE id = '{W.MembershipCarol}'"));
    }

    [Fact]
    public async Task E2_a_revoked_membership_can_never_be_reactivated()
    {
        await using (var carol = await AsApp(W.Carol))
        {
            await carol.ExecAsync($"SELECT nina.leave_baby('{W.BabyA}')");
            await carol.CommitAsync();
        }

        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, $"UPDATE nina.caregiver_membership SET status = 'ACTIVE', revoked_at = NULL WHERE id = '{W.MembershipCarol}'"));
        // nem o dono do schema, nem com a ficha de funcoes: REVOKED/DECLINED sao terminais
        Assert.Equal("NN051", await FailsAsync(Role.Owner, null, $"UPDATE nina.caregiver_membership SET status = 'ACTIVE', revoked_at = NULL WHERE id = '{W.MembershipCarol}'"));
        Assert.Equal("NN051", await FailsAsync(Role.Owner, null, Arm + $"UPDATE nina.caregiver_membership SET status = 'ACTIVE', revoked_at = NULL WHERE id = '{W.MembershipCarol}'"));
        await using var s = await AsApp(W.Carol);
        Assert.Equal(0, await s.ScalarAsync<long>("SELECT count(*) FROM nina.baby"));            // sem acesso depois de sair
    }

    [Fact]
    public async Task E3_the_membership_cannot_be_moved_to_another_baby_or_reassigned_to_another_user()
    {
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, $"UPDATE nina.caregiver_membership SET baby_id = '{W.BabyB}', role = 'CAREGIVER' WHERE user_id = '{W.Carol}'"));
        Assert.Equal("NN051", await FailsAsync(Role.Owner, null, Arm + $"UPDATE nina.caregiver_membership SET baby_id = '{W.BabyB}' WHERE id = '{W.MembershipCarol}'"));
        Assert.Equal("NN051", await FailsAsync(Role.Owner, null, Arm + $"UPDATE nina.caregiver_membership SET user_id = '{W.Dave}' WHERE id = '{W.MembershipCarol}'"));
        await using var s = await AsApp(W.Carol);
        Assert.Equal("BebeAlice", await s.ScalarAsync<string>("SELECT display_name FROM nina.baby"));
        Assert.Equal(1, await s.ScalarAsync<long>("SELECT count(*) FROM nina.baby"));        // nunca le o bebe do Bob
    }

    [Fact]
    public async Task Membership_changes_without_the_function_token_are_rejected_even_for_the_schema_owner()
    {
        Assert.Equal("NN051", await FailsAsync(Role.Owner, null, $"UPDATE nina.caregiver_membership SET role = 'CAREGIVER' WHERE id = '{W.MembershipCarol}'"));
        Assert.Equal("NN051", await FailsAsync(Role.Owner, null, $"UPDATE nina.caregiver_membership SET status = 'REVOKED', revoked_at = now() WHERE id = '{W.MembershipCarol}'"));
        // com a ficha, a promocao a OWNER ainda exige a ficha de propriedade
        Assert.Equal("NN050", await FailsAsync(Role.Owner, null, Arm + $"UPDATE nina.caregiver_membership SET role = 'OWNER' WHERE id = '{W.MembershipErin}'"));
        // um papel de aplicacao nao consegue forjar a ficha: nem a funcao nem o GUC servem
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, "SELECT nina.guard_arm('membership')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol,
            $"SELECT set_config('nina.guard.membership', 'on', true); UPDATE nina.caregiver_membership SET role = 'CAREGIVER' WHERE user_id = '{W.Carol}'"));
        Assert.Equal("NN051", await FailsAsync(Role.Owner, null,
            $"SELECT set_config('nina.guard.membership', 'on', true); UPDATE nina.caregiver_membership SET role = 'CAREGIVER' WHERE id = '{W.MembershipCarol}'"));
    }

    [Fact]
    public async Task The_last_active_owner_cannot_leave_or_be_deleted_SEC007()
    {
        Assert.Equal("NN010", await FailsOnCommitAsync(Role.Owner, null, $"DELETE FROM nina.caregiver_membership WHERE id = '{W.MembershipAlice}'"));
        await using var alice = await AsApp(W.Alice);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => alice.ExecAsync($"SELECT nina.leave_baby('{W.BabyA}')"));
        Assert.Equal("NN058", ex.SqlState);
    }

    // ---------------------------------------------------------------- SR-009 / convites

    [Fact]
    public async Task M1_an_owner_can_only_create_pending_invites_never_active_or_owner_memberships()
    {
        string Insert(string role, string status, string accepted) =>
            $"INSERT INTO nina.caregiver_membership (baby_id, user_id, role, status, accepted_at) VALUES ('{W.BabyB}', '{W.Dave}', '{role}', '{status}', {accepted})";
        Assert.Equal("42501", await FailsAsync(Role.App, W.Bob, Insert("CAREGIVER", "ACTIVE", "now()")));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Bob, Insert("OWNER", "ACTIVE", "now()")));
        await InviteAsync(W.Bob, W.BabyB, "dave@example.org");                    // convite pendente: ok
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave,
            $"INSERT INTO nina.caregiver_membership (baby_id, invited_email, role, status, invited_by, invite_token_hash, invite_expires_at) VALUES ('{W.BabyB}', 'x@example.org', 'CAREGIVER', 'PENDING', '{W.Dave}', decode(repeat('ab', 32), 'hex'), now() + interval '1 day')"));
        Assert.Equal(0, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.caregiver_membership WHERE baby_id = '{W.BabyB}' AND user_id = '{W.Dave}' AND status = 'ACTIVE'"));
    }

    [Fact]
    public async Task I1_the_invitee_accepts_with_the_token_and_gets_a_recorded_consent()
    {
        var frank = await NewUserAsync("frank@example.org");
        var (id, hash) = await InviteAsync(W.Alice, W.BabyA, "frank@example.org");

        Assert.Equal("ACCEPTED", await AcceptAsync(frank, hash));

        Assert.Equal("ACTIVE", await OwnerScalarAsync<string>($"SELECT status FROM nina.caregiver_membership WHERE id = '{id}'"));
        Assert.Null(await OwnerScalarAsync<string>($"SELECT invited_email FROM nina.caregiver_membership WHERE id = '{id}'"));      // minimizacao
        Assert.Equal(1, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.consent_record WHERE user_id = '{frank}' AND source = 'INVITE_ACCEPT' AND subject_baby_id = '{W.BabyA}' AND status = 'GRANTED'"));
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'baby.invitation_accepted' AND is_critical AND chain_seq IS NOT NULL"));
        await using var s = await AsApp(frank);
        Assert.Equal("CAREGIVER", await s.ScalarAsync<string>($"SELECT nina.baby_role('{W.BabyA}')"));
        // uso unico
        Assert.Equal("NOT_FOUND", await AcceptAsync(W.Dave, hash));
        Assert.Equal("NOT_FOUND", await AcceptAsync(frank, hash));
    }

    [Fact]
    public async Task I1_an_invitation_is_invisible_to_everyone_but_resolvable_only_by_its_recipient()
    {
        var frank = await NewUserAsync("frank@example.org");
        var (_, hash) = await InviteAsync(W.Alice, W.BabyA, "frank@example.org");

        foreach (var user in new[] { W.Dave, W.Bob })
        {
            await using var s = await AsApp(user);
            Assert.Equal(0, await s.ScalarAsync<long>("SELECT count(*) FROM nina.caregiver_membership WHERE status = 'PENDING'"));
            Assert.Equal("42501", await FailsAsync(Role.App, user, "DELETE FROM nina.caregiver_membership WHERE invite_token_hash IS NOT NULL"));
            Assert.Empty(await s.RowsAsync("SELECT * FROM nina.inspect_invitation(@h)", Bytes("h", hash)));
        }

        await using var frankSession = await AsApp(frank);
        var preview = await frankSession.RowsAsync("SELECT role, inviter_display_name, baby_initial FROM nina.inspect_invitation(@h)", Bytes("h", hash));
        var row = Assert.Single(preview);
        Assert.Equal("CAREGIVER|Alice|B", string.Join('|', row));      // sem o nome completo do bebe
    }

    [Fact]
    public async Task A_wrong_recipient_gets_a_uniform_not_found_and_repeated_failures_expire_the_invitation()
    {
        var frank = await NewUserAsync("frank@example.org");
        var (id, hash) = await InviteAsync(W.Alice, W.BabyA, "frank@example.org");

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("NOT_FOUND", await AcceptAsync(W.Dave, hash));        // nao revela que o convite existe
        }

        Assert.Equal(5, await OwnerScalarAsync<short>($"SELECT invite_failed_attempts FROM nina.caregiver_membership WHERE id = '{id}'"));
        Assert.Equal("EXPIRED", await AcceptAsync(frank, hash));              // o legitimo tambem perde o convite: pedir outro
    }

    [Fact]
    public async Task Acceptance_requires_a_verified_email_the_current_consent_version_and_a_live_invitation()
    {
        var unverified = await NewUserAsync("eve@example.org", verified: false);
        var frank = await NewUserAsync("frank@example.org");
        var (_, h1) = await InviteAsync(W.Alice, W.BabyA, "eve@example.org");
        var (_, h2) = await InviteAsync(W.Alice, W.BabyA, "frank@example.org");
        var gina = await NewUserAsync("gina@example.org");
        var (id3, h3) = await InviteAsync(W.Alice, W.BabyA, "gina@example.org", "READ_ONLY");

        Assert.Equal("EMAIL_NOT_VERIFIED", await AcceptAsync(unverified, h1));
        Assert.Equal("CONSENT_REQUIRED", await AcceptAsync(frank, h2, version: "0.0.1"));
        await OwnerAsync(Arm + $"UPDATE nina.caregiver_membership SET invite_expires_at = now() - interval '1 minute' WHERE id = '{id3}'");
        Assert.Equal("EXPIRED", await AcceptAsync(gina, h3));
        Assert.Equal("ACCEPTED", await AcceptAsync(frank, h2));
    }

    [Fact]
    public async Task A_user_with_an_active_membership_cannot_accept_a_second_invitation_to_the_same_baby()
    {
        var (_, other) = await InviteAsync(W.Alice, W.BabyA, "erin@example.org");
        Assert.Equal("ALREADY_MEMBER", await AcceptAsync(W.Erin, other));
    }

    [Fact]
    public async Task The_invitee_can_decline_and_the_invitation_becomes_terminal()
    {
        var frank = await NewUserAsync("frank@example.org");
        var (id, hash) = await InviteAsync(W.Alice, W.BabyA, "frank@example.org");

        await using (var s = await AsApp(frank))
        {
            Assert.Equal("DECLINED", await s.ScalarAsync<string>("SELECT nina.decline_invitation(@h)", Bytes("h", hash)));
            await s.CommitAsync();
        }

        Assert.Equal("DECLINED", await OwnerScalarAsync<string>($"SELECT status FROM nina.caregiver_membership WHERE id = '{id}'"));
        Assert.Equal("NOT_FOUND", await AcceptAsync(frank, hash));
        Assert.Equal("NN051", await FailsAsync(Role.Owner, null, Arm + $"UPDATE nina.caregiver_membership SET status = 'ACTIVE', accepted_at = now() WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Invites_per_owner_per_day_are_limited_by_a_parameter()
    {
        await OwnerAsync($"SELECT set_config('nina.user_id', '{W.Alice}', true); UPDATE nina.app_parameter SET value = '2' WHERE param_key = 'invites.max_per_day'");
        await InviteAsync(W.Alice, W.BabyA, "a1@example.org");
        await InviteAsync(W.Alice, W.BabyA, "a2@example.org");

        await using var s = await AsApp(W.Alice);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InviteAsync(W.Alice, W.BabyA, "a3@example.org"));
        Assert.Equal("NN057", ex.SqlState);
        Assert.Equal(0, await s.ScalarAsync<long>("SELECT count(*) FROM nina.caregiver_membership WHERE invited_email = 'a3@example.org'"));
    }

    [Fact]
    public async Task Invited_email_must_be_normalized()
    {
        Assert.Equal("23514", await FailsAsync(Role.App, W.Alice,
            $"INSERT INTO nina.caregiver_membership (baby_id, invited_email, role, status, invited_by, invite_token_hash, invite_expires_at) VALUES ('{W.BabyA}', 'MixedCase@Example.org', 'CAREGIVER', 'PENDING', '{W.Alice}', decode(repeat('cd', 32), 'hex'), now() + interval '1 day')"));
    }

    // ---------------------------------------------------------------- SR-004 / funcoes

    [Fact]
    public async Task T2_a_member_leaves_the_baby_and_loses_access_immediately()
    {
        await using (var erin = await AsApp(W.Erin))
        {
            await erin.ExecAsync($"SELECT nina.leave_baby('{W.BabyA}')");
            await erin.CommitAsync();
        }

        Assert.Equal("REVOKED", await OwnerScalarAsync<string>($"SELECT status FROM nina.caregiver_membership WHERE id = '{W.MembershipErin}'"));
        Assert.Equal("LEFT", await OwnerScalarAsync<string>($"SELECT revoked_reason FROM nina.caregiver_membership WHERE id = '{W.MembershipErin}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Erin,
            $"INSERT INTO nina.sleep_session (id, baby_id, start_at, tz, sleep_type, source) VALUES (gen_random_uuid(), '{W.BabyA}', now(), 'UTC', 'NAP', 'MANUAL')"));
        Assert.Equal("NN015", await FailsAsync(Role.App, W.Dave, $"SELECT nina.leave_baby('{W.BabyA}')"));
    }

    [Fact]
    public async Task The_owner_removes_a_caregiver_or_revokes_a_pending_invite_but_nobody_else_can()
    {
        var (pending, _) = await InviteAsync(W.Alice, W.BabyA, "frank@example.org");
        Assert.Equal("NN059", await FailsAsync(Role.App, W.Erin, $"SELECT nina.remove_member('{W.MembershipCarol}')"));
        Assert.Equal("NN059", await FailsAsync(Role.App, W.Bob, $"SELECT nina.remove_member('{W.MembershipCarol}')"));
        Assert.Equal("NN051", await FailsAsync(Role.App, W.Alice, $"SELECT nina.remove_member('{W.MembershipAlice}')"));

        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync($"SELECT nina.remove_member('{W.MembershipErin}')");
            await alice.ExecAsync($"SELECT nina.remove_member('{pending}')");
            await alice.CommitAsync();
        }

        Assert.Equal("OWNER_REMOVED", await OwnerScalarAsync<string>($"SELECT revoked_reason FROM nina.caregiver_membership WHERE id = '{W.MembershipErin}'"));
        Assert.Equal("REVOKED", await OwnerScalarAsync<string>($"SELECT status FROM nina.caregiver_membership WHERE id = '{pending}'"));
        Assert.Null(await OwnerScalarAsync<byte[]>($"SELECT invite_token_hash FROM nina.caregiver_membership WHERE id = '{pending}'"));
    }

    [Fact]
    public async Task The_owner_changes_a_role_between_caregiver_and_read_only_never_to_owner()
    {
        Assert.Equal("NN059", await FailsAsync(Role.App, W.Carol, $"SELECT nina.set_member_role('{W.MembershipCarol}', 'CAREGIVER')"));
        Assert.Equal("NN051", await FailsAsync(Role.App, W.Alice, $"SELECT nina.set_member_role('{W.MembershipCarol}', 'OWNER')"));
        Assert.Equal("NN051", await FailsAsync(Role.App, W.Alice, $"SELECT nina.set_member_role('{W.MembershipAlice}', 'CAREGIVER')"));
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync($"SELECT nina.set_member_role('{W.MembershipCarol}', 'CAREGIVER')");
            await alice.CommitAsync();
        }

        Assert.Equal("CAREGIVER", await OwnerScalarAsync<string>($"SELECT role FROM nina.caregiver_membership WHERE id = '{W.MembershipCarol}'"));
    }

    [Fact]
    public async Task T3_ownership_transfers_atomically_under_the_application_role()
    {
        // sem reautenticacao comprovada: recusado
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, $"SELECT nina.transfer_ownership('{W.BabyA}', '{W.MembershipErin}', decode(repeat('00', 32), 'hex'))"));
        // escopo errado nao serve
        var wrongScope = await ReauthAsync(W.Alice, "ACCOUNT_DELETE");
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, $"SELECT nina.transfer_ownership('{W.BabyA}', '{W.MembershipErin}', @h)", Bytes("h", wrongScope)));
        // so o Owner transfere; destino precisa ser membro ativo nao-Owner do mesmo bebe
        var jtiErin = await ReauthAsync(W.Erin, "OWNERSHIP_TRANSFER");
        Assert.Equal("NN059", await FailsAsync(Role.App, W.Erin, $"SELECT nina.transfer_ownership('{W.BabyA}', '{W.MembershipCarol}', @h)", Bytes("h", jtiErin)));
        var jti = await ReauthAsync(W.Alice, "OWNERSHIP_TRANSFER");
        Assert.Equal("NN051", await FailsAsync(Role.App, W.Alice, $"SELECT nina.transfer_ownership('{W.BabyA}', '{W.MembershipBob}', @h)", Bytes("h", jti)));
        Assert.Equal("NN051", await FailsAsync(Role.App, W.Alice, $"SELECT nina.transfer_ownership('{W.BabyA}', '{W.MembershipAlice}', @h)", Bytes("h", jti)));

        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync($"SELECT nina.transfer_ownership('{W.BabyA}', '{W.MembershipErin}', @h)", Bytes("h", jti));
            await alice.CommitAsync();
        }

        Assert.Equal("CAREGIVER", await OwnerScalarAsync<string>($"SELECT role FROM nina.caregiver_membership WHERE id = '{W.MembershipAlice}'"));
        Assert.Equal("OWNER", await OwnerScalarAsync<string>($"SELECT role FROM nina.caregiver_membership WHERE id = '{W.MembershipErin}'"));
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'baby.ownership_transferred' AND is_critical"));
        Assert.Equal(W.Erin, await OwnerScalarAsync<Guid>($"SELECT f.owner_user_id FROM nina.baby b JOIN nina.family f ON f.id = b.family_id WHERE b.id = '{W.BabyA}'"));
        // o novo Owner edita o perfil; o antigo deixa de poder
        await using (var erin = await AsApp(W.Erin))
        {
            Assert.Equal(1, await erin.ExecAsync($"UPDATE nina.baby SET display_name = 'NovoNome' WHERE id = '{W.BabyA}'"));
        }

        await using var aliceAfter = await AsApp(W.Alice);
        Assert.Equal(0, await aliceAfter.ExecAsync($"UPDATE nina.baby SET display_name = 'X' WHERE id = '{W.BabyA}'"));
        // o jti vale uma vez so
        var replay = await FailsAsync(Role.App, W.Erin, $"SELECT nina.transfer_ownership('{W.BabyA}', '{W.MembershipAlice}', @h)", Bytes("h", jti));
        Assert.Equal("NN014", replay);
    }

    [Fact]
    public async Task Members_see_each_others_display_name_only_through_the_member_refs_function()
    {
        await using var carol = await AsApp(W.Carol);
        Assert.Equal(1, await carol.ScalarAsync<long>("SELECT count(*) FROM nina.app_user"));        // so a propria linha
        var refs = await carol.RowsAsync($"SELECT display_name FROM nina.baby_member_refs('{W.BabyA}') ORDER BY 1");
        Assert.Equal("Alice,Carol,Erin", string.Join(',', refs.Select(r => r[0])));
        Assert.Empty(await carol.RowsAsync($"SELECT * FROM nina.baby_member_refs('{W.BabyB}')"));
    }
}
