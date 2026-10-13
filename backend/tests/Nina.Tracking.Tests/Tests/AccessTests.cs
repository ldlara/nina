using System.Net;
using System.Text.Json.Nodes;
using Nina.Tracking.Tests.Infrastructure;

namespace Nina.Tracking.Tests.Tests;

/// <summary>IDOR/BOLA (Anexo B 9, SR-014): vínculo e papel conferidos no banco; baby_id e entity_id validados juntos; 404 uniforme sem oráculo.</summary>
public sealed class AccessTests(PostgresFixture postgres) : TrackingTestBase(postgres)
{
    private sealed record World(Session Ana, Session Rita, Session Cat, Session Bob, Session Zed, Guid Baby, Guid ZedBaby, Guid Diaper, Guid Sleep, long DiaperVersion);

    private async Task<World> BuildAsync()
    {
        var ana = await UserAsync();
        var rita = await UserAsync();
        var cat = await UserAsync();
        var bob = await UserAsync();
        var zed = await UserAsync();
        var baby = await BabyAsync(ana);
        var zedBaby = await BabyAsync(zed);
        await MemberAsync(baby, rita, "READ_ONLY");
        await MemberAsync(baby, cat, "CAREGIVER");
        var bobLink = await MemberAsync(baby, bob, "CAREGIVER");
        var diaper = Guid.NewGuid();
        var sleep = Guid.NewGuid();
        var created = await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, diaper, Mut.DiaperData(T0, notes: "segredo")), Mut.Create(Mut.Sleep, baby, sleep, Mut.SleepData(T0, T0.AddHours(1))));
        await RemoveMemberAsync(bobLink, ana);
        return new World(ana, rita, cat, bob, zed, baby, zedBaby, diaper, sleep, created[0].Version());
    }

    [Fact]
    public async Task Push_by_a_stranger_is_BABY_NOT_FOUND_exactly_like_a_baby_that_does_not_exist_and_writes_nothing()
    {
        var w = await BuildAsync();
        var real = (await PushOkAsync(w.Zed, Mut.Create(Mut.Diaper, w.Baby, Guid.NewGuid(), Mut.DiaperData(T0))))[0];
        var ghost = (await PushOkAsync(w.Zed, Mut.Create(Mut.Diaper, Guid.NewGuid(), Guid.NewGuid(), Mut.DiaperData(T0))))[0];
        Assert.Equal("BABY_NOT_FOUND", real.ProblemCode());
        Assert.Equal(real!["problem"]!.ToJsonString(), ghost!["problem"]!.ToJsonString());
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.diaper_event WHERE baby_id = '{w.Baby}'"));
        Assert.Equal(0L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.sync_mutation WHERE user_id = '{w.Zed.UserId}'"));
    }

    [Fact]
    public async Task Read_only_members_cannot_write_and_revoked_members_learn_it_per_mutation()
    {
        var w = await BuildAsync();
        var ro = (await PushOkAsync(w.Rita, Mut.Update(Mut.Diaper, w.Baby, w.Diaper, w.DiaperVersion, new JsonObject { ["notes"] = "x" }), Mut.Delete(Mut.Diaper, w.Baby, w.Diaper, w.DiaperVersion)));
        Assert.All(ro, r => Assert.Equal("FORBIDDEN_ROLE", r.ProblemCode()));
        var revoked = await PushOkAsync(w.Bob, Mut.Create(Mut.Diaper, w.Baby, Guid.NewGuid(), Mut.DiaperData(T0)));
        Assert.Equal("ACCESS_REVOKED", revoked[0].ProblemCode());
        Assert.False(revoked[0]!["retryable"]!.GetValue<bool>());
        Assert.Equal("segredo", await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE id = '{w.Diaper}'"));
    }

    [Fact]
    public async Task An_entity_of_another_baby_is_indistinguishable_from_a_missing_one_on_update_delete_and_wake()
    {
        var w = await BuildAsync();
        var results = await PushOkAsync(
            w.Zed,
            Mut.Update(Mut.Diaper, w.ZedBaby, w.Diaper, 1, new JsonObject { ["notes"] = "invadido" }),
            Mut.Update(Mut.Diaper, w.ZedBaby, Guid.NewGuid(), 1, new JsonObject { ["notes"] = "invadido" }),
            Mut.Delete(Mut.Diaper, w.ZedBaby, w.Diaper, 1),
            Mut.Delete(Mut.Diaper, w.ZedBaby, Guid.NewGuid(), 1),
            Mut.Create(Mut.Wake, w.ZedBaby, Guid.NewGuid(), Mut.WakeData(w.Sleep, T0, T0.AddMinutes(5))),
            Mut.Create(Mut.Wake, w.ZedBaby, Guid.NewGuid(), Mut.WakeData(Guid.NewGuid(), T0, T0.AddMinutes(5))));
        Assert.All(results, r => Assert.Equal("ENTITY_NOT_FOUND", r.ProblemCode()));
        Assert.Equal(results[0]!["problem"]!.ToJsonString(), results[1]!["problem"]!.ToJsonString());
        Assert.Equal(results[4]!["problem"]!.ToJsonString(), results[5]!["problem"]!.ToJsonString());
        Assert.Equal("segredo", await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE id = '{w.Diaper}'"));
        Assert.Null(await AdminScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.diaper_event WHERE id = '{w.Diaper}'"));
    }

    [Fact]
    public async Task Creating_with_an_id_used_in_another_baby_neither_collides_nor_leaks_it()
    {
        var w = await BuildAsync();
        var result = (await PushOkAsync(w.Zed, Mut.Create(Mut.Diaper, w.ZedBaby, w.Diaper, Mut.DiaperData(T0, notes: "meu"))))[0];
        Assert.Equal("APPLIED", result.Status());                       // PK escopada por bebê (R-04): o id do outro tenant é irrelevante
        Assert.Equal("segredo", await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE baby_id = '{w.Baby}' AND id = '{w.Diaper}'"));
        Assert.Equal("meu", await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE baby_id = '{w.ZedBaby}' AND id = '{w.Diaper}'"));
    }

    [Fact]
    public async Task A_mixed_batch_is_authorized_per_baby()
    {
        var w = await BuildAsync();
        var results = await PushOkAsync(
            w.Zed,
            Mut.Create(Mut.Diaper, w.Baby, Guid.NewGuid(), Mut.DiaperData(T0)),
            Mut.Create(Mut.Diaper, w.ZedBaby, Guid.NewGuid(), Mut.DiaperData(T0)));
        Assert.Equal(["REJECTED", "APPLIED"], results.Select(r => r.Status()).ToArray());
    }

    [Fact]
    public async Task Every_read_endpoint_hides_a_stranger_behind_the_same_404_as_a_missing_baby()
    {
        var w = await BuildAsync();
        var ghost = Guid.NewGuid();
        string[] Paths(Guid baby, Guid ev) =>
        [
            $"/v1/babies/{baby}/timeline",
            $"/v1/babies/{baby}/events/{ev}",
            $"/v1/babies/{baby}/events/{ev}/history",
            $"/v1/babies/{baby}/aggregates?period=WEEK",
            $"/v1/sync/pull?baby_id={baby}",
        ];
        var real = Paths(w.Baby, w.Diaper);
        var fake = Paths(ghost, w.Diaper);
        for (var i = 0; i < real.Length; i++)
        {
            var a = await Api.GetAsync(real[i], w.Zed.AccessToken);
            var b = await Api.GetAsync(fake[i], w.Zed.AccessToken);
            Assert.Equal(HttpStatusCode.NotFound, a.Status);
            Assert.Equal("NOT_FOUND", a.Code);
            Assert.Equal(b.Status, a.Status);
            Assert.Equal(b.Code, a.Code);
        }

        // evento do bebê A pedido pelo caminho do bebê do Zed: também 404
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync($"/v1/babies/{w.ZedBaby}/events/{w.Diaper}", w.Zed.AccessToken)).Status);
    }

    [Fact]
    public async Task Revoked_members_get_403_ACCESS_REVOKED_on_every_read_endpoint()
    {
        var w = await BuildAsync();
        foreach (var path in new[]
        {
            $"/v1/babies/{w.Baby}/timeline", $"/v1/babies/{w.Baby}/events/{w.Diaper}", $"/v1/babies/{w.Baby}/events/{w.Diaper}/history",
            $"/v1/babies/{w.Baby}/aggregates?period=DAY", $"/v1/sync/pull?baby_id={w.Baby}",
        })
        {
            var response = await Api.GetAsync(path, w.Bob.AccessToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.Status);
            Assert.Equal("ACCESS_REVOKED", response.Code);
        }
    }

    [Fact]
    public async Task Read_only_members_read_everything_except_the_event_history()
    {
        var w = await BuildAsync();
        foreach (var path in new[] { $"/v1/babies/{w.Baby}/timeline", $"/v1/babies/{w.Baby}/events/{w.Diaper}", $"/v1/babies/{w.Baby}/aggregates?period=DAY", $"/v1/sync/pull?baby_id={w.Baby}" })
        {
            Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync(path, w.Rita.AccessToken)).Status);
        }

        var history = await Api.GetAsync($"/v1/babies/{w.Baby}/events/{w.Diaper}/history", w.Rita.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, history.Status);
        Assert.Equal("FORBIDDEN_ROLE", history.Code);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync($"/v1/babies/{w.Baby}/events/{w.Diaper}/history", w.Cat.AccessToken)).Status);
    }

    [Fact]
    public async Task A_pull_cursor_of_another_baby_or_user_is_invalid_not_a_leak()
    {
        var w = await BuildAsync();
        var (_, zedCursor) = await PullAllAsync(w.Zed, w.ZedBaby);
        var cross = await PullAsync(w.Cat, w.Baby, zedCursor);
        Assert.Equal(HttpStatusCode.Gone, cross.Status);
        Assert.Equal("INVALID", cross.Json!["reason"]!.GetValue<string>());
        var (_, anaCursor) = await PullAllAsync(w.Ana, w.Baby);
        Assert.Equal(HttpStatusCode.Gone, (await PullAsync(w.Zed, w.ZedBaby, anaCursor)).Status);
    }
}
