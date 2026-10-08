using Nina.Database.Tests.Infrastructure;
using Npgsql;

namespace Nina.Database.Tests.Tests;

/// <summary>
/// NR-06 (reteste): <c>created_by</c> e <c>last_modified_by</c> são fixados por gatilho a partir do contexto (<c>nina.user_id</c>); o cliente não os
/// escolhe no INSERT nem os altera no UPDATE, e o <c>change_log.actor_user_id</c> reflete o autor real (não-repúdio na disputa de guarda).
/// </summary>
[Collection(PgClusterGroup.Name)]
public sealed class AuthorshipTests(PgCluster cluster) : DbTestBase(cluster)
{
    private static string Sleep(Guid id, Guid baby, string createdBy, string modifiedBy) =>
        $"INSERT INTO nina.sleep_session (id, baby_id, start_at, end_at, tz, sleep_type, source, created_by, last_modified_by) VALUES ('{id}', '{baby}', now() - interval '3 hours', now() - interval '2 hours', 'UTC', 'NAP', 'MANUAL', {createdBy}, {modifiedBy})";

    [Fact]
    public async Task NR06_the_insert_cannot_claim_another_user_as_author_and_the_change_log_records_the_real_actor()
    {
        // O ataque do reteste: Erin (CAREGIVER do bebe A) grava um sono dizendo que quem criou e modificou foi a Alice.
        var id = Guid.NewGuid();
        await using (var erin = await AsApp(W.Erin))
        {
            await erin.ExecAsync(Sleep(id, W.BabyA, $"'{W.Alice}'", $"'{W.Alice}'"));
            await erin.CommitAsync();
        }

        Assert.Equal(W.Erin, await OwnerScalarAsync<Guid>($"SELECT created_by FROM nina.sleep_session WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
        Assert.Equal(W.Erin, await OwnerScalarAsync<Guid>($"SELECT last_modified_by FROM nina.sleep_session WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
        Assert.Equal(W.Erin, await OwnerScalarAsync<Guid>($"SELECT actor_user_id FROM nina.change_log WHERE baby_id = '{W.BabyA}' AND entity_id = '{id}'"));
        // autoria nula tambem vira o autor real
        var anonymous = Guid.NewGuid();
        await using (var erin = await AsApp(W.Erin))
        {
            await erin.ExecAsync(Sleep(anonymous, W.BabyA, "NULL", "NULL"));
            await erin.CommitAsync();
        }

        Assert.Equal(W.Erin, await OwnerScalarAsync<Guid>($"SELECT created_by FROM nina.sleep_session WHERE baby_id = '{W.BabyA}' AND id = '{anonymous}'"));
    }

    [Fact]
    public async Task NR06_last_modified_by_follows_the_actor_of_each_update_and_created_by_never_changes()
    {
        var id = Guid.NewGuid();
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync(Sleep(id, W.BabyA, $"'{W.Alice}'", $"'{W.Alice}'"));
            await alice.CommitAsync();
        }

        // o cliente nao escolhe last_modified_by (coluna fora do GRANT) nem created_by
        Assert.Equal("42501", await FailsAsync(Role.App, W.Erin, $"UPDATE nina.sleep_session SET last_modified_by = '{W.Alice}' WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Erin, $"UPDATE nina.sleep_session SET created_by = '{W.Erin}' WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
        await using (var erin = await AsApp(W.Erin))
        {
            Assert.Equal(1, await erin.ExecAsync($"UPDATE nina.sleep_session SET notes = 'editado' WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
            await erin.CommitAsync();
        }

        Assert.Equal(W.Alice, await OwnerScalarAsync<Guid>($"SELECT created_by FROM nina.sleep_session WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
        Assert.Equal(W.Erin, await OwnerScalarAsync<Guid>($"SELECT last_modified_by FROM nina.sleep_session WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
        Assert.Equal(W.Erin, await OwnerScalarAsync<Guid>($"SELECT actor_user_id FROM nina.change_log WHERE baby_id = '{W.BabyA}' AND entity_id = '{id}' ORDER BY sync_sequence DESC LIMIT 1"));
        // nem o dono do schema (sem contexto de usuario) reescreve a autoria de criacao
        await OwnerAsync($"UPDATE nina.sleep_session SET created_by = '{W.Bob}' WHERE baby_id = '{W.BabyA}' AND id = '{id}'");
        Assert.Equal(W.Alice, await OwnerScalarAsync<Guid>($"SELECT created_by FROM nina.sleep_session WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
    }

    [Fact]
    public async Task NR06_every_synced_table_and_the_baby_profile_fix_authorship_from_the_context()
    {
        string Insert(string table, string columns, string values) =>
            $"INSERT INTO nina.{table} (id, baby_id, {columns}, tz, created_by, last_modified_by) VALUES (gen_random_uuid(), '{W.BabyA}', {values}, 'UTC', '{W.Alice}', '{W.Alice}')";
        await using (var erin = await AsApp(W.Erin))
        {
            await erin.ExecAsync(Insert("feeding_session", "feeding_type, start_at, end_at, side", "'BREASTFEEDING', now() - interval '1 hour', now() - interval '50 minutes', 'LEFT'"));
            await erin.ExecAsync(Insert("pumping_session", "start_at", "now() - interval '2 hours'"));
            await erin.ExecAsync(Insert("diaper_event", "occurred_at, diaper_type", "now(), 'WET'"));
            await erin.CommitAsync();
        }

        foreach (var table in new[] { "feeding_session", "pumping_session", "diaper_event" })
        {
            Assert.Equal(W.Erin, await OwnerScalarAsync<Guid>($"SELECT created_by FROM nina.{table} WHERE baby_id = '{W.BabyA}'"));
            Assert.Equal(W.Erin, await OwnerScalarAsync<Guid>($"SELECT last_modified_by FROM nina.{table} WHERE baby_id = '{W.BabyA}'"));
        }

        // perfil do bebe: o Owner edita e vira o ultimo a modificar; criar bebe com autoria de terceiro grava o criador real
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync($"UPDATE nina.baby SET display_name = 'Novo nome' WHERE id = '{W.BabyA}'");
            await alice.CommitAsync();
        }

        Assert.Equal(W.Alice, await OwnerScalarAsync<Guid>($"SELECT last_modified_by FROM nina.baby WHERE id = '{W.BabyA}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, $"UPDATE nina.baby SET last_modified_by = '{W.Bob}' WHERE id = '{W.BabyA}'"));
        var newFamily = Guid.NewGuid();
        var newBaby = Guid.NewGuid();
        await using (var dave = await AsApp(W.Dave))
        {
            await dave.ExecAsync($"INSERT INTO nina.family (id, owner_user_id) VALUES ('{newFamily}', '{W.Dave}')");
            await dave.ExecAsync($"INSERT INTO nina.baby (id, family_id, display_name, birth_date, timezone, created_by, last_modified_by) VALUES ('{newBaby}', '{newFamily}', 'Dado', current_date - 5, 'UTC', '{W.Bob}', '{W.Bob}')");
            await dave.CommitAsync();
        }

        Assert.Equal(W.Dave, await OwnerScalarAsync<Guid>($"SELECT created_by FROM nina.baby WHERE id = '{newBaby}'"));
        Assert.Equal(W.Dave, await OwnerScalarAsync<Guid>($"SELECT last_modified_by FROM nina.baby WHERE id = '{newBaby}'"));
    }

    [Fact]
    public async Task NR06_the_wake_events_cascaded_from_a_deleted_session_carry_the_deleting_actor()
    {
        var session = Guid.NewGuid();
        var wake = Guid.NewGuid();
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync(Sleep(session, W.BabyA, $"'{W.Alice}'", $"'{W.Alice}'"));
            await alice.ExecAsync(
                $"INSERT INTO nina.wake_event (id, baby_id, sleep_session_id, started_at, ended_at, tz, source) VALUES ('{wake}', '{W.BabyA}', '{session}', now() - interval '150 minutes', now() - interval '140 minutes', 'UTC', 'MANUAL')");
            await alice.CommitAsync();
        }

        await using (var erin = await AsApp(W.Erin))
        {
            await erin.ExecAsync($"UPDATE nina.sleep_session SET deleted_at = now() WHERE baby_id = '{W.BabyA}' AND id = '{session}'");
            await erin.CommitAsync();
        }

        Assert.Equal(W.Erin, await OwnerScalarAsync<Guid>($"SELECT last_modified_by FROM nina.wake_event WHERE baby_id = '{W.BabyA}' AND id = '{wake}'"));
        Assert.Equal(W.Alice, await OwnerScalarAsync<Guid>($"SELECT created_by FROM nina.wake_event WHERE baby_id = '{W.BabyA}' AND id = '{wake}'"));
    }
}
