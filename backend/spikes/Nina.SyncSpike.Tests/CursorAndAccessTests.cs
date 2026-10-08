using System.Text.Json.Nodes;
using Nina.SyncSpike.Harness;
using Nina.SyncSpike.Server;
using Xunit;

namespace Nina.SyncSpike.Tests;

public sealed class CursorCodecTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Roundtrip_preserves_map_and_snapshot_position()
    {
        var c = new CursorCodec(Key);
        var b1 = Guid.NewGuid(); var b2 = Guid.NewGuid();
        var p = new CursorPayload(1_760_000_000, new Dictionary<Guid, CursorEntry> { [b1] = new(42, "abcDEF12"), [b2] = new(7, "zzzz") }, new SnapshotPosition(3, Guid.NewGuid()));
        var back = c.TryDecode(c.Encode(p))!;
        Assert.Equal(p.IssuedAtUnix, back.IssuedAtUnix);
        Assert.Equal(42, back.Babies[b1].Seq);
        Assert.Equal("zzzz", back.Babies[b2].Epoch);
        Assert.Equal(p.Snapshot, back.Snapshot);
    }

    [Fact]
    public void Tampered_or_garbage_cursors_are_rejected()
    {
        var c = new CursorCodec(Key);
        var s = c.Encode(new CursorPayload(1, new Dictionary<Guid, CursorEntry> { [Guid.NewGuid()] = new(1, "e") }));
        Assert.Null(c.TryDecode(s[..^1] + (s[^1] == 'A' ? 'B' : 'A')));
        Assert.Null(c.TryDecode(s.Replace(s.Split('.')[0][..4], "AAAA")));
        Assert.Null(c.TryDecode("lixo"));
        Assert.Null(c.TryDecode(""));
        Assert.Null(c.TryDecode(new string('x', 600)));
        Assert.Null(new CursorCodec(new byte[32]).TryDecode(s));              // outra chave
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Size_stays_under_contract_limit_of_512_chars(int babies)
    {
        var c = new CursorCodec(Key);
        var map = Enumerable.Range(0, babies).ToDictionary(_ => Guid.NewGuid(), _ => new CursorEntry(123_456_789, CursorCodec.EpochOf(Guid.NewGuid())));
        var s = c.Encode(new CursorPayload(1_760_000_000, map, new SnapshotPosition(5, Guid.NewGuid())));
        Assert.True(s.Length < 512, $"{babies} bebês => {s.Length} caracteres");
    }
}

[Collection("pg")]
public sealed class CursorTests(SpikeFixture fx)
{
    private SpikeEnv Env => fx.Env;
    private static DateTimeOffset T(int m) => Data.T0.AddMinutes(m);

    private static async Task<SyncProblemException> Pull410(SyncService svc, FakeDevice d, string? cursor) =>
        await Assert.ThrowsAsync<SyncProblemException>(() => svc.PullAsync(d.Auth, d.BabyId, cursor));

    [Fact]
    public async Task Cursor_older_than_90_days_returns_410_and_device_does_full_resync()
    {
        var t = await Trio.CreateAsync(Env);
        t.A.Create("DIAPER_EVENT", Data.Diaper(T(1)));
        await t.A.SyncAsync();
        var old = t.A.Cursor;
        try
        {
            Env.Clock.Offset = TimeSpan.FromDays(89);
            await Env.Service.PullAsync(t.A.Auth, t.Baby.BabyId, old);                           // 89 dias: ainda delta
            Env.Clock.Offset = TimeSpan.FromDays(91);
            var ex = await Pull410(Env.Service, t.A, old);
            Assert.Equal((410, "SYNC_CURSOR_EXPIRED", "EXPIRED", true), (ex.Problem.Status, ex.Problem.Code, ex.Problem.Reason, ex.Problem.ResyncRequired));
            t.A.Create("PUMPING_SESSION", Data.Pump(T(50)));                                     // pendente durante a expiração
            var rep = await t.A.SyncAsync();
            Assert.True(rep.Resynced);
            Assert.Equal(1, t.A.FullResyncs);
            Assert.Empty(t.A.Outbox);
        }
        finally { Env.Clock.Offset = TimeSpan.Zero; }
        await t.B.SyncAsync();
        await t.AssertConvergedAsync(t.A, t.B);
    }

    [Fact]
    public async Task Cursor_below_purged_through_returns_410_pending_mutations_survive_and_missing_entities_are_rejected()
    {
        var t = await Trio.CreateAsync(Env);
        var e1 = t.A.Create("DIAPER_EVENT", Data.Diaper(T(1), "WET"));
        var e2 = t.A.Create("SLEEP_SESSION", Data.Sleep(T(10), T(40)));
        await t.SyncBothTwiceAsync();
        var bCursor = t.B.Cursor;
        t.B.Online = false;
        t.B.Update("DIAPER_EVENT", e1, FakeDevice.D(("diaper_type", "DRY")));         // aponta para entidade que será purgada
        var fresh = t.B.Create("DIAPER_EVENT", Data.Diaper(T(70), "MIXED"));
        t.A.Delete("DIAPER_EVENT", e1);
        t.A.Update("SLEEP_SESSION", e2, FakeDevice.D(("notes", "depois")));
        t.A.Create("FEEDING_SESSION", Data.Bottle(T(90)));
        await t.A.SyncAsync();
        var head = (await Env.LogStatsAsync(t.Baby.BabyId)).Head;

        var purged = await Env.AgeAndPurgeAsync(t.Baby.BabyId, 91);
        Assert.True(purged.ChangeLog >= head);
        Assert.Equal(1, purged.Tombstones);
        var st = await Env.LogStatsAsync(t.Baby.BabyId);
        Assert.Equal((head, 0L), (st.PurgedThrough, st.Count));
        Assert.Equal(0, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.diaper_event WHERE id=@i", ("i", e1)));   // linha física purgada

        // A está em dia (cursor == purged_through): continua em delta, sem resync
        await t.A.SyncAsync();
        Assert.Equal(0, t.A.FullResyncs);
        // B ficou para trás
        var ex = await Pull410(Env.Service, t.B, bCursor);
        Assert.Equal("EXPIRED", ex.Problem.Reason);
        t.B.Online = true;
        var rep = await t.B.SyncAsync();
        Assert.True(rep.Resynced);
        Assert.Equal(1, t.B.FullResyncs);
        var up = rep.Results.First(r => r.EntityId == e1);
        Assert.Equal(("REJECTED", "ENTITY_NOT_FOUND", false), (up.Status, up.Problem!.Code, up.Retryable ?? true));
        Assert.Equal("APPLIED", rep.Results.First(r => r.EntityId == fresh).Status);                   // a fila foi preservada e entregue
        await t.A.SyncAsync(); await t.B.SyncAsync();
        await t.AssertConvergedAsync(t.A, t.B);
        Assert.NotNull(t.A.Get("DIAPER_EVENT", fresh));
    }

    [Fact]
    public async Task Invalid_cursors_return_410_invalid()
    {
        var t = await Trio.CreateAsync(Env);
        var other = await Env.CreateBabyAsync(t.UserA);
        t.A.Create("DIAPER_EVENT", Data.Diaper(T(1)));
        await t.SyncBothTwiceAsync();
        var otherDevice = t.NewDevice("A2", t.UserA);
        var otherPull = await Env.Service.PullAsync(t.A.Auth, other.BabyId, null);

        async Task Expect(string label, FakeDevice d, Guid baby, string cursor)
        {
            var ex = await Assert.ThrowsAsync<SyncProblemException>(() => Env.Service.PullAsync(d.Auth, baby, cursor));
            Assert.True(ex.Problem is { Status: 410, Code: "SYNC_CURSOR_EXPIRED", Reason: "INVALID" }, label);
        }
        var good = t.A.Cursor!;
        await Expect("adulterado", t.A, t.Baby.BabyId, good[..^2] + (good[^2] == 'A' ? "BB" : "AA"));
        await Expect("lixo", t.A, t.Baby.BabyId, "nao-e-um-cursor");
        await Expect("de outro bebê", t.A, t.Baby.BabyId, otherPull.NextCursor);
        await Expect("de outro usuário (época do vínculo)", t.B, t.Baby.BabyId, good);
        var future = Env.Service.Cursors.Encode(new CursorPayload(DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            new Dictionary<Guid, CursorEntry> { [t.Baby.BabyId] = new(10_000, CursorCodec.EpochOf(Guid.NewGuid())) }));
        await Expect("do futuro / época errada", t.A, t.Baby.BabyId, future);
        // "do futuro" com época correta (ex.: banco restaurado de backup antigo): sequência > last_sequence
        var membership = await Env.ScalarAsync<Guid>("SELECT id FROM nina.caregiver_membership WHERE baby_id=@b AND user_id=@u", ("b", t.Baby.BabyId), ("u", t.UserA));
        var future2 = Env.Service.Cursors.Encode(new CursorPayload(DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            new Dictionary<Guid, CursorEntry> { [t.Baby.BabyId] = new(10_000, CursorCodec.EpochOf(membership)) }));
        await Expect("sequência maior que o head", t.A, t.Baby.BabyId, future2);
        _ = otherDevice;
    }

    [Fact]
    public async Task Cursor_size_for_real_pull_is_small()
    {
        var t = await Trio.CreateAsync(Env);
        var page = await Env.Service.PullAsync(t.A.Auth, t.Baby.BabyId, null);
        Assert.InRange(page.NextCursor.Length, 50, 300);
    }
}

[Collection("pg")]
public sealed class AccessTests(SpikeFixture fx)
{
    private SpikeEnv Env => fx.Env;
    private static DateTimeOffset T(int m) => Data.T0.AddMinutes(m);

    [Fact]
    public async Task Revoked_caregiver_gets_403_on_pull_and_push_and_cursor_dies_with_the_membership()
    {
        var t = await Trio.CreateAsync(Env);
        t.A.Create("DIAPER_EVENT", Data.Diaper(T(1)));
        await t.SyncBothTwiceAsync();
        var oldCursor = t.B.Cursor!;
        t.B.Online = false;
        var pendingId = t.B.Create("DIAPER_EVENT", Data.Diaper(T(5), "DIRTY"));          // criado offline antes de saber da revogação
        await Env.RevokeAsync(t.Baby.BabyId, t.UserB);
        t.B.Online = true;

        var ex = await Assert.ThrowsAsync<SyncProblemException>(() => Env.Service.PullAsync(t.B.Auth, t.Baby.BabyId, oldCursor));
        Assert.Equal((403, "ACCESS_REVOKED"), (ex.Problem.Status, ex.Problem.Code));
        var rep = await t.B.SyncAsync();
        var r = Assert.Single(rep.Results);
        Assert.Equal(("REJECTED", "ACCESS_REVOKED", false), (r.Status, r.Problem!.Code, r.Retryable ?? true));
        Assert.True(t.B.Revoked);
        Assert.Empty(t.B.Local);                                                          // cliente apaga o cache local (SEC-005)
        Assert.Empty(t.B.Outbox);
        Assert.Equal(0, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.diaper_event WHERE id=@i", ("i", pendingId)));
        Assert.Equal(0, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.sync_mutation WHERE user_id=@u AND baby_id=@b", ("u", t.UserB), ("b", t.Baby.BabyId)));

        // A segue normal e cria dado durante a revogação
        var during = t.A.Create("DIAPER_EVENT", Data.Diaper(T(9)));
        await t.A.SyncAsync();

        // reconvite = vínculo novo => o cursor antigo é inválido (época diferente) e o aparelho refaz o snapshot
        await Env.AddMemberAsync(t.Baby.BabyId, t.UserB, "CAREGIVER");
        var ex2 = await Assert.ThrowsAsync<SyncProblemException>(() => Env.Service.PullAsync(t.B.Auth, t.Baby.BabyId, oldCursor));
        Assert.Equal("INVALID", ex2.Problem.Reason);
        var b2 = t.NewDevice("B2", t.UserB);
        await b2.SyncAsync();
        Assert.NotNull(b2.Get("DIAPER_EVENT", during));
        await t.A.SyncAsync();
        await t.AssertConvergedAsync(t.A, b2);
    }

    [Fact]
    public async Task Read_only_member_pulls_but_cannot_push_and_outsider_sees_nothing()
    {
        var t = await Trio.CreateAsync(Env, "READ_ONLY");
        var id = t.A.Create("DIAPER_EVENT", Data.Diaper(T(1)));
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.NotNull(t.B.Get("DIAPER_EVENT", id));                                       // lê
        t.B.Create("DIAPER_EVENT", Data.Diaper(T(2)));
        var rep = await t.B.SyncAsync();
        Assert.Equal("FORBIDDEN_ROLE", Assert.Single(rep.Results).Problem!.Code);

        var outsider = t.NewDevice("X", await Env.CreateUserAsync());
        var ex = await Assert.ThrowsAsync<SyncProblemException>(() => Env.Service.PullAsync(outsider.Auth, t.Baby.BabyId, null));
        Assert.Equal(404, ex.Problem.Status);
        outsider.Create("DIAPER_EVENT", Data.Diaper(T(3)));
        Assert.Equal("BABY_NOT_FOUND", Assert.Single((await outsider.SyncAsync()).Results).Problem!.Code);
    }

    [Fact]
    public async Task Envelope_limits_and_validation()
    {
        var t = await Trio.CreateAsync(Env);
        Mutation M(int i) => new(Guid.NewGuid(), "CREATE", "DIAPER_EVENT", Guid.NewGuid(), t.Baby.BabyId, 0, Data.T0, Data.Diaper(T(i)));
        var e100 = await Env.Service.PushAsync(t.A.Auth, new PushRequest(t.A.DeviceId, Enumerable.Range(0, 100).Select(M).ToList()));
        Assert.All(e100.Results, r => Assert.Equal("APPLIED", r.Status));
        var e101 = await Assert.ThrowsAsync<SyncProblemException>(() => Env.Service.PushAsync(t.A.Auth, new PushRequest(t.A.DeviceId, Enumerable.Range(0, 101).Select(M).ToList())));
        Assert.Equal(400, e101.Problem.Status);
        var empty = await Assert.ThrowsAsync<SyncProblemException>(() => Env.Service.PushAsync(t.A.Auth, new PushRequest(t.A.DeviceId, [])));
        Assert.Equal(400, empty.Problem.Status);
        var big = await Assert.ThrowsAsync<SyncProblemException>(() => Env.Service.PushAsync(t.A.Auth, new PushRequest(t.A.DeviceId, [M(1)]), 256 * 1024 + 1));
        Assert.Equal((413, "PAYLOAD_TOO_LARGE"), (big.Problem.Status, big.Problem.Code));
        var dev = await Assert.ThrowsAsync<SyncProblemException>(() => Env.Service.PushAsync(t.A.Auth, new PushRequest(Guid.NewGuid(), [M(1)])));
        Assert.Equal(400, dev.Problem.Status);
        foreach (var bad in new[] { 0, 501 })
        {
            var pe = await Assert.ThrowsAsync<SyncProblemException>(() => Env.Service.PullAsync(t.A.Auth, t.Baby.BabyId, null, bad));
            Assert.Equal(400, pe.Problem.Status);
        }
        // lote de 100 + pull por páginas de 500 funcionam nos limites
        var p = await Env.Service.PullAsync(t.A.Auth, t.Baby.BabyId, null, 500);
        Assert.Equal(101, p.Changes.Count);                                                 // 100 fraldas + BABY
        Assert.False(p.HasMore);
    }

    [Fact]
    public async Task Contract_vs_database_divergences_are_observable()
    {
        var t = await Trio.CreateAsync(Env);
        Mutation M(string type, object data) => new(Guid.NewGuid(), "CREATE", type, Guid.NewGuid(), t.Baby.BabyId, 0, Data.T0, Json.ToElement(data));
        async Task<MutationResult> One(Mutation m) => (await Env.Service.PushAsync(t.A.Auth, new PushRequest(t.A.DeviceId, [m]))).Results.Single();

        // contrato: method_or_place maxLength 80; banco: <= 60  => 61..80 caracteres passam no contrato e quebram no banco
        var place70 = await One(M("SLEEP_SESSION", new Dictionary<string, object?> { ["sleep_type"] = "NAP", ["start_at"] = T(1), ["tz"] = "UTC", ["source"] = "MANUAL", ["method_or_place"] = new string('m', 70) }));
        Assert.Equal("VALIDATION_FAILED", place70.Problem!.Code);
        // contrato: notes maxLength 500; banco: <= 2000 => 501..2000 passam no banco (o contrato não é imposto pelo servidor se ninguém validar)
        var notes900 = await One(M("DIAPER_EVENT", new Dictionary<string, object?> { ["occurred_at"] = T(1), ["tz"] = "UTC", ["diaper_type"] = "WET", ["notes"] = new string('n', 900) }));
        Assert.Equal("APPLIED", notes900.Status);
        // contrato: WakeEventData sem tz; banco: tz NOT NULL => o servidor precisa derivar da sessão (feito no spike)
        var s = Guid.NewGuid();
        await Env.Service.PushAsync(t.A.Auth, new PushRequest(t.A.DeviceId, [new Mutation(Guid.NewGuid(), "CREATE", "SLEEP_SESSION", s, t.Baby.BabyId, 0, Data.T0, Data.Sleep(T(0), T(60), "NIGHT"))]));
        var wake = await One(M("WAKE_EVENT", new Dictionary<string, object?> { ["sleep_session_id"] = s, ["started_at"] = T(10), ["ended_at"] = T(20) }));
        Assert.Equal("APPLIED", wake.Status);
        Assert.Equal("America/Sao_Paulo", await Env.ScalarAsync<string>("SELECT tz FROM nina.wake_event WHERE id=@i", ("i", wake.EntityId)));
        // BREASTFEEDING sem end_at: contrato e banco concordam (ADR-0010)
        var bf = await One(M("FEEDING_SESSION", new Dictionary<string, object?> { ["feeding_type"] = "BREASTFEEDING", ["start_at"] = T(1), ["tz"] = "UTC", ["side"] = "LEFT" }));
        Assert.Equal("VALIDATION_FAILED", bf.Problem!.Code);
        // campo desconhecido (additionalProperties:false)
        var unk = await One(M("DIAPER_EVENT", new Dictionary<string, object?> { ["occurred_at"] = T(1), ["tz"] = "UTC", ["diaper_type"] = "WET", ["cor"] = "azul" }));
        Assert.Equal("VALIDATION_FAILED", unk.Problem!.Code);
    }
}
