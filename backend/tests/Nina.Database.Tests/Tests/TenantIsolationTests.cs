using Nina.Database.Tests.Infrastructure;
using Npgsql;

namespace Nina.Database.Tests.Tests;

/// <summary>SR-001 (CRÍTICO): isolamento por bebê não pode ser contornado por <c>family</c>, <c>baby_select</c> nem <c>family_id</c> mutável. Anexo A: Z0/Z1, T0, N3, B1/B2, F1.</summary>
[Collection(PgClusterGroup.Name)]
public sealed class TenantIsolationTests(PgCluster cluster) : DbTestBase(cluster)
{
    [Fact]
    public async Task Z0_without_context_nothing_is_visible_and_Z1_a_malformed_context_fails_closed()
    {
        await using var none = await AsApp(null);
        Assert.Equal(0, await none.ScalarAsync<long>("SELECT count(*) FROM nina.baby"));
        Assert.Equal(0, await none.ScalarAsync<long>("SELECT count(*) FROM nina.family"));
        Assert.Equal(0, await none.ScalarAsync<long>("SELECT count(*) FROM nina.app_user"));
        Assert.Equal(0, await none.ScalarAsync<long>("SELECT count(*) FROM nina.sleep_session"));

        await using var bad = await AsApp(null);
        await bad.ExecAsync("SELECT set_config('nina.user_id', 'abc', true)");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => bad.ScalarAsync<long>("SELECT count(*) FROM nina.baby"));
        Assert.Equal("22P02", ex.SqlState);
    }

    [Fact]
    public async Task T0_a_read_only_member_reads_but_never_writes_the_babys_events()
    {
        await using (var carol = await AsApp(W.Carol))
        {
            Assert.Equal(1, await carol.ScalarAsync<long>("SELECT count(*) FROM nina.baby"));
            Assert.Equal("BebeAlice", await carol.ScalarAsync<string>("SELECT display_name FROM nina.baby"));
        }

        var sql = $"INSERT INTO nina.sleep_session (id, baby_id, start_at, tz, sleep_type, source) VALUES (gen_random_uuid(), '{W.BabyA}', now(), 'UTC', 'NAP', 'MANUAL')";
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, sql));
        Assert.Null(await FailsAsync(Role.App, W.Erin, sql));                      // CAREGIVER escreve
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, sql));            // sem vinculo
        await using var update = await AsApp(W.Carol);
        Assert.Equal(0, await update.ExecAsync($"UPDATE nina.baby SET display_name = 'x' WHERE id = '{W.BabyA}'"));
    }

    [Fact]
    public async Task N3_nobody_can_take_over_a_family_and_read_other_childrens_profiles()
    {
        // Dave, sem vinculo algum, tenta o ataque do SR-001: virar dono da familia do Bob e ler o bebe.
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"UPDATE nina.family SET owner_user_id = '{W.Dave}' WHERE id = '{W.FamilyB}'"));
        await using (var dave = await AsApp(W.Dave))
        {
            Assert.Equal(0, await dave.ScalarAsync<long>("SELECT count(*) FROM nina.family"));            // nao enumera familias
            Assert.Equal(0, await dave.ScalarAsync<long>("SELECT count(*) FROM nina.baby"));
        }

        // Defesa em profundidade: nem o dono do schema muda a titularidade fora de transfer_ownership/erase_user (NN050).
        Assert.Equal("NN050", await FailsAsync(Role.Owner, null, $"UPDATE nina.family SET owner_user_id = '{W.Dave}' WHERE id = '{W.FamilyB}'"));
        Assert.Equal(W.Bob, await OwnerScalarAsync<Guid>($"SELECT owner_user_id FROM nina.family WHERE id = '{W.FamilyB}'"));
    }

    [Fact]
    public async Task Each_user_sees_only_their_own_family()
    {
        await using var alice = await AsApp(W.Alice);
        Assert.Equal(W.FamilyA, await alice.ScalarAsync<Guid>("SELECT id FROM nina.family"));
        await using var erin = await AsApp(W.Erin);
        Assert.Equal(0, await erin.ScalarAsync<long>("SELECT count(*) FROM nina.family"));   // cuidador nao e dono de familia
    }

    [Fact]
    public async Task B1_B2_family_id_of_a_baby_is_immutable_for_the_app_and_for_the_owner_of_the_schema()
    {
        var sql = $"UPDATE nina.baby SET family_id = '{W.FamilyB}' WHERE id = '{W.BabyA}'";
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, sql));       // coluna fora do GRANT UPDATE
        Assert.Equal("NN050", await FailsAsync(Role.Owner, null, sql));        // trigger baby_a_family_guard
        await using var bob = await AsApp(W.Bob);
        Assert.Equal(0, await bob.ScalarAsync<long>($"SELECT count(*) FROM nina.baby WHERE id = '{W.BabyA}'"));
    }

    [Fact]
    public async Task The_owner_edits_the_profile_but_caregivers_and_read_only_cannot()
    {
        Assert.Null(await FailsAsync(Role.App, W.Alice, $"UPDATE nina.baby SET display_name = 'Novo' WHERE id = '{W.BabyA}'"));
        foreach (var user in new[] { W.Erin, W.Carol, W.Dave })
        {
            await using var s = await AsApp(user);
            Assert.Equal(0, await s.ExecAsync($"UPDATE nina.baby SET display_name = 'Hack' WHERE id = '{W.BabyA}'"));
        }

        Assert.Equal("BebeAlice", await OwnerScalarAsync<string>($"SELECT display_name FROM nina.baby WHERE id = '{W.BabyA}'"));
    }

    [Fact]
    public async Task Bootstrap_a_user_creates_family_baby_and_becomes_the_owner_in_one_transaction()
    {
        var baby = Guid.NewGuid();
        var family = Guid.NewGuid();
        await using (var dave = await AsApp(W.Dave))
        {
            await dave.ExecAsync($"INSERT INTO nina.family (id, owner_user_id) VALUES ('{family}', '{W.Dave}')");
            // RETURNING exige que o bebe recem-criado seja visivel: so no instante de criacao (bebe da propria familia sem Owner)
            Assert.Equal(baby, await dave.ScalarAsync<Guid>(
                $"INSERT INTO nina.baby (id, family_id, display_name, birth_date, timezone) VALUES ('{baby}', '{family}', 'BebeDave', current_date - 5, 'UTC') RETURNING id"));
            await dave.ExecAsync(
                $"INSERT INTO nina.caregiver_membership (baby_id, user_id, role, status, accepted_at) VALUES ('{baby}', '{W.Dave}', 'OWNER', 'ACTIVE', now())");
            await dave.CommitAsync();
        }

        await using var again = await AsApp(W.Dave);
        Assert.Equal("BebeDave", await again.ScalarAsync<string>("SELECT display_name FROM nina.baby"));
        Assert.Equal("OWNER", await again.ScalarAsync<string>($"SELECT nina.baby_role('{baby}')"));
    }

    [Fact]
    public async Task Bootstrap_cannot_be_abused_to_claim_babies_of_other_families_or_a_second_owner()
    {
        // bebe na familia de outro
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave,
            $"INSERT INTO nina.baby (id, family_id, display_name, birth_date, timezone) VALUES (gen_random_uuid(), '{W.FamilyA}', 'x', current_date - 5, 'UTC')"));
        // familia em nome de outro
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"INSERT INTO nina.family (owner_user_id) VALUES ('{W.Bob}')"));
        // autoproclamar-se Owner de bebe alheio
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave,
            $"INSERT INTO nina.caregiver_membership (baby_id, user_id, role, status, accepted_at) VALUES ('{W.BabyB}', '{W.Dave}', 'OWNER', 'ACTIVE', now())"));
        // o dono da familia que ja tem Owner (inclusive revogado) nao reabre o bootstrap
        await using var bob = await AsApp(W.Bob);
        Assert.False(await bob.ScalarAsync<bool>($"SELECT nina.can_bootstrap_owner('{W.BabyB}')"));
    }

    [Fact]
    public async Task F1_the_former_owner_loses_the_profile_when_ownership_moves_with_the_baby_to_another_family()
    {
        var jti = await ReauthAsync(W.Alice, "OWNERSHIP_TRANSFER");
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync($"SELECT nina.transfer_ownership('{W.BabyA}', '{W.MembershipErin}', @h)", Bytes("h", jti));
            await alice.CommitAsync();
        }

        Assert.NotEqual(W.FamilyA, await OwnerScalarAsync<Guid>($"SELECT family_id FROM nina.baby WHERE id = '{W.BabyA}'"));
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync($"SELECT nina.leave_baby('{W.BabyA}')");
            await alice.CommitAsync();
        }

        await using var after = await AsApp(W.Alice);
        // Alice continua dona da familia A, mas ja nao le o perfil (antes: baby_select por "dono da familia")
        Assert.Equal(W.FamilyA, await after.ScalarAsync<Guid>("SELECT id FROM nina.family"));
        Assert.Equal(0, await after.ScalarAsync<long>("SELECT count(*) FROM nina.baby"));
        Assert.Equal(0, await after.ScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{W.BabyA}'"));
    }

    [Fact]
    public async Task F1_owning_the_family_alone_never_grants_the_babys_profile_without_an_active_membership()
    {
        // O ataque original: titular da familia (ou quem a tomou) leria o perfil pela clausula "dono da familia" do baby_select.
        // Aqui o baby segue na familia da Alice, mas o vinculo dela foi encerrado (Erin e a Owner): Alice nao le nada do bebe.
        await OwnerAsync(
            $"""
            SELECT nina.guard_arm('membership'), nina.guard_arm('ownership');
            UPDATE nina.caregiver_membership SET role = 'CAREGIVER' WHERE id = '{W.MembershipAlice}';
            UPDATE nina.caregiver_membership SET role = 'OWNER' WHERE id = '{W.MembershipErin}';
            UPDATE nina.caregiver_membership SET status = 'REVOKED', revoked_at = now(), revoked_reason = 'OWNERSHIP_TRANSFERRED' WHERE id = '{W.MembershipAlice}';
            """);
        Assert.Equal(W.FamilyA, await OwnerScalarAsync<Guid>($"SELECT family_id FROM nina.baby WHERE id = '{W.BabyA}'"));
        await using var alice = await AsApp(W.Alice);
        Assert.Equal(W.FamilyA, await alice.ScalarAsync<Guid>("SELECT id FROM nina.family"));         // continua titular da familia
        Assert.Equal(0, await alice.ScalarAsync<long>("SELECT count(*) FROM nina.baby"));
        Assert.Equal(0, await alice.ExecAsync($"UPDATE nina.baby SET display_name = 'Hack' WHERE id = '{W.BabyA}'"));
        Assert.Equal("BebeAlice", await OwnerScalarAsync<string>($"SELECT display_name FROM nina.baby WHERE id = '{W.BabyA}'"));
    }

    [Fact]
    public async Task Entities_are_scoped_by_baby_a_foreign_baby_id_never_reaches_another_tenants_row()
    {
        // R-04: o mesmo id pode existir em dois bebes; e UPDATE/DELETE com baby_id alheio nao encontra linha (nada vaza)
        await using var alice = await AsApp(W.Alice);
        Assert.Null(await FailsAsync(Role.App, W.Alice,
            $"INSERT INTO nina.sleep_session (id, baby_id, start_at, tz, sleep_type, source) VALUES ('{W.SleepB}', '{W.BabyA}', now(), 'UTC', 'NAP', 'MANUAL')"));
        Assert.Equal(0, await alice.ExecAsync($"UPDATE nina.sleep_session SET notes = 'hack' WHERE baby_id = '{W.BabyB}' AND id = '{W.SleepB}'"));
        Assert.Equal(0, await alice.ScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{W.BabyB}'"));
        Assert.Equal("nota privada do Bob", await OwnerScalarAsync<string>($"SELECT notes FROM nina.sleep_session WHERE baby_id = '{W.BabyB}' AND id = '{W.SleepB}'"));
    }
}
