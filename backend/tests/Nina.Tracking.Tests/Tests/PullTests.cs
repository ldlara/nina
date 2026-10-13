using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Nina.Tracking.Sync;
using Nina.Tracking.Tests.Infrastructure;

namespace Nina.Tracking.Tests.Tests;

/// <summary>BE-004b: snapshot e delta por cursor assinado, tombstones no feed, 410 com resync, revogação e re-convite (INV-14).</summary>
public sealed class PullTests(PostgresFixture postgres) : TrackingTestBase(postgres)
{
    private static Dictionary<Guid, (long Version, bool Deleted)> Apply(Dictionary<Guid, (long Version, bool Deleted)> state, IEnumerable<JsonNode> changes)
    {
        foreach (var c in changes)
        {
            var id = c.EntityId();
            var version = c["version"]!.GetValue<long>();
            if (state.TryGetValue(id, out var local) && local.Version >= version)
            {
                continue;                                                    // aplicação idempotente por versão
            }

            state[id] = (version, c.Op() == "TOMBSTONE");
        }

        return state;
    }

    [Fact]
    public async Task A_paged_snapshot_with_writes_between_pages_plus_the_following_delta_equals_the_server_state()
    {
        var ana = await UserAsync();
        var bia = await UserAsync();
        var baby = await BabyAsync(ana);
        await MemberAsync(baby, bia, "CAREGIVER");
        var ids = Enumerable.Range(0, 45).Select(_ => Guid.NewGuid()).ToList();
        await PushOkAsync(ana, [.. ids.Select((id, i) => Mut.Create(Mut.Diaper, baby, id, Mut.DiaperData(T0.AddMinutes(i))))]);
        await PushOkAsync(ana, Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(T0, T0.AddHours(1))));

        var state = new Dictionary<Guid, (long Version, bool Deleted)>();
        string? cursor = null;
        var pages = 0;
        var modes = new HashSet<string>();
        while (true)
        {
            var page = await PullAsync(bia, baby, cursor, 10);
            Assert.Equal(HttpStatusCode.OK, page.Status);
            OpenApiContract.AssertPullResponse(page.Json);
            modes.Add(page.Json!["mode"]!.GetValue<string>());
            Apply(state, page.Json["changes"]!.AsArray().Select(c => c!.DeepClone()));
            cursor = page.Json["next_cursor"]!.GetValue<string>();
            pages++;
            if (pages == 2)
            {
                // enquanto o snapshot do outro aparelho está no meio: cria, edita e exclui
                await PushOkAsync(
                    ana,
                    Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0.AddHours(5))),
                    Mut.Update(Mut.Diaper, baby, ids[3], 1, new JsonObject { ["notes"] = "editada no meio" }),
                    Mut.Delete(Mut.Diaper, baby, ids[40], 1));
            }

            if (!page.Json["has_more"]!.GetValue<bool>())
            {
                break;
            }
        }

        Assert.Equal(["SNAPSHOT"], modes);
        var (delta, final) = await PullAllAsync(bia, baby, cursor);
        Apply(state, delta);

        var server = await AdminRowsAsync(
            $"SELECT id, version, deleted_at IS NOT NULL FROM nina.diaper_event WHERE baby_id = '{baby}' UNION ALL SELECT id, version, deleted_at IS NOT NULL FROM nina.sleep_session WHERE baby_id = '{baby}'");
        foreach (var row in server)
        {
            Assert.Equal(((long)row[1]!, (bool)row[2]!), state[(Guid)row[0]!]);
        }

        Assert.Equal(server.Count + 1, state.Count);                         // + o perfil do bebê
        var again = await PullAsync(bia, baby, final);
        Assert.Equal("DELTA", again.Json!["mode"]!.GetValue<string>());
        Assert.Empty(again.Json["changes"]!.AsArray());
        Assert.False(again.Json["has_more"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_snapshot_has_the_baby_with_my_role_and_only_live_entities()
    {
        var ana = await UserAsync();
        var rita = await UserAsync();
        var baby = await BabyAsync(ana);
        await MemberAsync(baby, rita, "READ_ONLY");
        var gone = Guid.NewGuid();
        var created = await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, gone, Mut.DiaperData(T0)), Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)));
        await PushOkAsync(ana, Mut.Delete(Mut.Diaper, baby, gone, created[0].Version()));
        var (changes, _) = await PullAllAsync(rita, baby);
        Assert.DoesNotContain(changes, c => c.EntityId() == gone);
        var babyEntity = changes.Single(c => c.EntityType() == "BABY")["entity"]!;
        Assert.Equal("READ_ONLY", babyEntity["my_role"]!.GetValue<string>());
        Assert.Equal(baby.ToString(), babyEntity["id"]!.GetValue<string>());
        Assert.Equal(100, babyEntity["age_calculation"]!["chronological_days"]!.GetValue<int>());          // 100 dias de vida; termo 10 dias depois
        Assert.True(babyEntity["age_calculation"]!["correction_applied"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_delta_collapses_repeated_updates_and_carries_tombstones()
    {
        var ana = await UserAsync();
        var bia = await UserAsync();
        var baby = await BabyAsync(ana);
        await MemberAsync(baby, bia, "CAREGIVER");
        var id = Guid.NewGuid();
        var doomed = Guid.NewGuid();
        var created = await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, id, Mut.DiaperData(T0)), Mut.Create(Mut.Diaper, baby, doomed, Mut.DiaperData(T0)));
        var (_, cursor) = await PullAllAsync(bia, baby);
        for (var i = 0; i < 5; i++)
        {
            await PushOkAsync(ana, Mut.Update(Mut.Diaper, baby, id, created[0].Version(), new JsonObject { ["notes"] = "n" + i }));
        }

        await PushOkAsync(ana, Mut.Delete(Mut.Diaper, baby, doomed, created[1].Version()));
        var (delta, _) = await PullAllAsync(bia, baby, cursor);
        Assert.Equal(2, delta.Count);                                          // 6 mudanças, 2 entidades
        Assert.Equal("n4", delta.Single(c => c.EntityId() == id)["entity"]!["notes"]!.GetValue<string>());
        var tombstone = delta.Single(c => c.EntityId() == doomed);
        Assert.Equal("TOMBSTONE", tombstone.Op());
        Assert.NotNull(tombstone["deleted_at"]);
        Assert.Null(tombstone["entity"]);
    }

    [Fact]
    public async Task A_small_limit_pages_the_delta_without_losing_or_repeating_changes()
    {
        var ana = await UserAsync();
        var bia = await UserAsync();
        var baby = await BabyAsync(ana);
        await MemberAsync(baby, bia, "CAREGIVER");
        var (_, cursor) = await PullAllAsync(bia, baby);
        var ids = Enumerable.Range(0, 13).Select(_ => Guid.NewGuid()).ToList();
        await PushOkAsync(ana, [.. ids.Select(id => Mut.Create(Mut.Diaper, baby, id, Mut.DiaperData(T0)))]);
        var (delta, _) = await PullAllAsync(bia, baby, cursor, limit: 4);
        Assert.Equal(ids.Order().ToArray(), delta.Select(c => c.EntityId()).Order().ToArray());
    }

    [Theory]
    [InlineData("lixo")]
    [InlineData("abc.def")]
    [InlineData("e30.AAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Garbage_cursors_are_410_INVALID_with_resync_required(string cursor)
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var response = await PullAsync(ana, baby, cursor);
        Assert.Equal(HttpStatusCode.Gone, response.Status);
        Assert.Equal("SYNC_CURSOR_EXPIRED", response.Code);
        Assert.Equal("INVALID", response.Json!["reason"]!.GetValue<string>());
        Assert.True(response.Json["resync_required"]!.GetValue<bool>());
        OpenApiContract.AssertValid("Problem", response.Json);
    }

    [Fact]
    public async Task A_tampered_cursor_is_invalid_but_a_cursor_from_the_future_or_with_the_wrong_epoch_too()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var (_, cursor) = await PullAllAsync(ana, baby);
        var flipped = cursor[..^2] + (cursor[^2] == 'A' ? 'B' : 'A') + cursor[^1];
        Assert.Equal("INVALID", (await PullAsync(ana, baby, flipped)).Json!["reason"]!.GetValue<string>());
        Assert.Equal("INVALID", (await PullAsync(ana, baby, cursor + "x")).Json!["reason"]!.GetValue<string>());

        var codec = Factory.Services.GetRequiredService<CursorCodec>();
        var membership = await AdminScalarAsync<Guid>($"SELECT id FROM nina.caregiver_membership WHERE baby_id = '{baby}' AND user_id = '{ana.UserId}'");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string Make(long sequence, string epoch, long issued) =>
            codec.Encode(new CursorPayload(issued, new Dictionary<Guid, CursorEntry> { [baby] = new CursorEntry(sequence, epoch) }));
        var epoch = CursorCodec.EpochOf(membership);
        Assert.Equal("INVALID", (await PullAsync(ana, baby, Make(10_000, epoch, now))).Json!["reason"]!.GetValue<string>());       // do futuro
        Assert.Equal("INVALID", (await PullAsync(ana, baby, Make(1, "outra-epoca", now))).Json!["reason"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await PullAsync(ana, baby, Make(1, epoch, now))).Status);                                // válido
    }

    [Fact]
    public async Task A_cursor_older_than_ninety_days_is_410_EXPIRED_and_a_full_resync_keeps_working()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)));
        var codec = Factory.Services.GetRequiredService<CursorCodec>();
        var membership = await AdminScalarAsync<Guid>($"SELECT id FROM nina.caregiver_membership WHERE baby_id = '{baby}' AND user_id = '{ana.UserId}'");
        string Aged(int days) => codec.Encode(new CursorPayload(
            DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds(), new Dictionary<Guid, CursorEntry> { [baby] = new CursorEntry(1, CursorCodec.EpochOf(membership)) }));

        var expired = await PullAsync(ana, baby, Aged(91));
        Assert.Equal(HttpStatusCode.Gone, expired.Status);
        Assert.Equal("EXPIRED", expired.Json!["reason"]!.GetValue<string>());
        Assert.True(expired.Json["resync_required"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await PullAsync(ana, baby, Aged(89))).Status);

        var (changes, cursor) = await PullAllAsync(ana, baby);                // resync completo
        Assert.NotEmpty(changes);
        Assert.Equal(HttpStatusCode.OK, (await PullAsync(ana, baby, cursor)).Status);
    }

    [Fact]
    public async Task After_the_purge_only_cursors_below_the_floor_expire_and_a_device_that_is_up_to_date_keeps_the_delta()
    {
        var ana = await UserAsync();
        var bia = await UserAsync();
        var baby = await BabyAsync(ana);
        await MemberAsync(baby, bia, "CAREGIVER");
        await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)));
        var (_, stale) = await PullAllAsync(bia, baby);
        await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)), Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)));
        var (_, current) = await PullAllAsync(ana, baby);

        await AdminExecAsync($"UPDATE nina.change_log SET changed_at = now() - interval '100 days' WHERE baby_id = '{baby}'");
        await AdminExecAsync("SELECT * FROM nina.purge_expired_sync_data(1000)");

        var expired = await PullAsync(bia, baby, stale);
        Assert.Equal(HttpStatusCode.Gone, expired.Status);
        Assert.Equal("EXPIRED", expired.Json!["reason"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await PullAsync(ana, baby, current)).Status);                 // em dia: segue em delta
        var (snapshot, _) = await PullAllAsync(bia, baby);
        Assert.Equal(4, snapshot.Count);                                                               // perfil + 3 fraldas
    }

    [Fact]
    public async Task Revoking_a_caregiver_cuts_pull_and_push_and_a_new_invite_invalidates_the_old_cursor()
    {
        var ana = await UserAsync();
        var bob = await UserAsync();
        var baby = await BabyAsync(ana);
        var link = await MemberAsync(baby, bob, "CAREGIVER");
        await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)));
        var (_, cursor) = await PullAllAsync(bob, baby);

        await RemoveMemberAsync(link, ana);
        var denied = await PullAsync(bob, baby, cursor);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("ACCESS_REVOKED", denied.Code);
        Assert.Equal("ACCESS_REVOKED", (await PushOkAsync(bob, Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0))))[0].ProblemCode());

        var during = Guid.NewGuid();
        await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, during, Mut.DiaperData(T0.AddHours(1))));          // criado durante a revogação
        await MemberAsync(baby, bob, "CAREGIVER");                                                              // convite refeito: vínculo novo
        var stale = await PullAsync(bob, baby, cursor);
        Assert.Equal(HttpStatusCode.Gone, stale.Status);
        Assert.Equal("INVALID", stale.Json!["reason"]!.GetValue<string>());
        var (resynced, _) = await PullAllAsync(bob, baby);
        Assert.Contains(resynced, c => c.EntityId() == during);
    }

    [Fact]
    public async Task Pull_parameters_are_validated()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.GetAsync("/v1/sync/pull", ana.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.GetAsync("/v1/sync/pull?baby_id=x", ana.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PullAsync(ana, baby, null, 0)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PullAsync(ana, baby, null, 501)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PullAsync(ana, baby, null, 500)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PullAsync(ana, baby, new string('a', 513))).Status);
    }
}
