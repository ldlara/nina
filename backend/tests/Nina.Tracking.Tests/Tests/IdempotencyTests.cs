using System.Text.Json.Nodes;
using Nina.Tracking.Tests.Infrastructure;

namespace Nina.Tracking.Tests.Tests;

/// <summary>INV-18 / RF-046-A3: idempotência por <c>mutation_id</c>, escopada por usuário e dispositivo, inclusive sob concorrência.</summary>
public sealed class IdempotencyTests(PostgresFixture postgres) : TrackingTestBase(postgres)
{
    [Fact]
    public async Task A_retry_after_a_lost_response_is_a_DUPLICATE_with_the_canonical_entity_and_creates_nothing_new()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var id = Guid.NewGuid();
        var mutation = Mut.Create(Mut.Diaper, baby, id, Mut.DiaperData(T0, "WET", "nota"));
        var first = (await PushOkAsync(ana, mutation))[0];
        var logBefore = await AdminScalarAsync<long>($"SELECT count(*) FROM nina.change_log WHERE baby_id = '{baby}'");
        var headBefore = await AdminScalarAsync<long>($"SELECT last_sequence FROM nina.baby_sync_head WHERE baby_id = '{baby}'");

        var retry = (await PushOkAsync(ana, mutation))[0];

        Assert.Equal("DUPLICATE", retry.Status());
        Assert.Equal(first.Version(), retry.Version());
        Assert.Equal("NONE", retry.Resolution());
        Assert.Equal(id.ToString(), retry.Entity()!["id"]!.GetValue<string>());          // estado canônico: o cliente reconcilia sem novo pull
        Assert.Equal("nota", retry.Entity()!["notes"]!.GetValue<string>());
        Assert.Equal(first["server_received_at"]!.GetValue<string>(), retry["server_received_at"]!.GetValue<string>());
        Assert.Equal(logBefore, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.change_log WHERE baby_id = '{baby}'"));
        Assert.Equal(headBefore, await AdminScalarAsync<long>($"SELECT last_sequence FROM nina.baby_sync_head WHERE baby_id = '{baby}'"));
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.diaper_event WHERE baby_id = '{baby}'"));
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.sync_mutation WHERE baby_id = '{baby}'"));
    }

    [Fact]
    public async Task A_whole_batch_can_be_resent_and_every_item_is_a_DUPLICATE()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var timer = Guid.NewGuid();
        var batch = new[]
        {
            Mut.Create(Mut.Sleep, baby, timer, Mut.SleepData(T0, null, source: "TIMER")),
            Mut.Update(Mut.Sleep, baby, timer, 1, new JsonObject { ["end_at"] = Mut.At(T0.AddHours(1)) }),
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)),
        };
        var first = await PushOkAsync(ana, batch);
        var second = await PushOkAsync(ana, batch);
        Assert.All(second, r => Assert.Equal("DUPLICATE", r.Status()));
        Assert.Equal(first.Select(r => r.Version()).ToArray(), second.Select(r => r.Version()).ToArray());
        // o DUPLICATE do update devolve o estado atual (com o fim da soneca)
        Assert.NotNull(second[1].Entity()!["end_at"]);
    }

    [Fact]
    public async Task The_DUPLICATE_replays_the_resolution_and_the_overlap_warning_of_the_first_response()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var a = Guid.NewGuid();
        await PushOkAsync(ana, Mut.Create(Mut.Sleep, baby, a, Mut.SleepData(T0, T0.AddHours(2))));
        var overlapping = Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(T0.AddHours(1), T0.AddHours(3)));
        var first = (await PushOkAsync(ana, overlapping))[0];
        Assert.Equal("KEPT_BOTH", first.Resolution());

        var retry = (await PushOkAsync(ana, overlapping))[0];
        Assert.Equal("DUPLICATE", retry.Status());
        Assert.Equal("KEPT_BOTH", retry.Resolution());
        Assert.Equal(["SLEEP_OVERLAP"], retry.WarningCodes());
        Assert.Equal(a.ToString(), retry["warnings"]![0]!["related_entity_ids"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Twelve_concurrent_identical_pushes_apply_exactly_once()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var id = Guid.NewGuid();
        var request = Mut.Push(ana.DeviceId, Mut.Create(Mut.Sleep, baby, id, Mut.SleepData(T0, T0.AddHours(1))));

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Api.PostAsync("/v1/sync/push", request.DeepClone(), ana.AccessToken)));

        Assert.All(responses, r => Assert.Equal(System.Net.HttpStatusCode.OK, r.Status));
        var statuses = responses.Select(r => r.Json!["results"]![0].Status()).ToList();
        Assert.Equal(1, statuses.Count(s => s == "APPLIED"));
        Assert.Equal(11, statuses.Count(s => s == "DUPLICATE"));
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.change_log WHERE baby_id = '{baby}' AND entity_id = '{id}'"));
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.sync_mutation WHERE entity_id = '{id}'"));
        Assert.Single(responses.Select(r => r.Json!["results"]![0].Version()).Distinct());
    }

    [Fact]
    public async Task The_mutation_id_is_scoped_by_user_and_device_and_never_burns_someone_elses()
    {
        var ana = await UserAsync();
        var bia = await UserAsync();
        var anaBaby = await BabyAsync(ana);
        var biaBaby = await BabyAsync(bia);
        var shared = Guid.NewGuid();

        // Bia usa o mesmo mutation_id que Ana vai usar: o da Ana não vira DUPLICATE nem é recusado
        var biaResult = (await PushOkAsync(bia, Mut.Create(Mut.Diaper, biaBaby, Guid.NewGuid(), Mut.DiaperData(T0), shared)))[0];
        var anaResult = (await PushOkAsync(ana, Mut.Create(Mut.Diaper, anaBaby, Guid.NewGuid(), Mut.DiaperData(T0), shared)))[0];
        Assert.Equal("APPLIED", biaResult.Status());
        Assert.Equal("APPLIED", anaResult.Status());

        // mesmo usuário, outro aparelho: escopo (usuário, dispositivo) também separa
        var anaPhone = await SecondDeviceAsync(ana);
        var otherDevice = (await PushOkAsync(anaPhone, Mut.Create(Mut.Diaper, anaBaby, Guid.NewGuid(), Mut.DiaperData(T0.AddMinutes(1)), shared)))[0];
        Assert.Equal("APPLIED", otherDevice.Status());
        Assert.Equal(3L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.sync_mutation WHERE mutation_id = '{shared}'"));
    }

    [Fact]
    public async Task Reusing_a_mutation_id_for_another_operation_is_rejected_and_applies_nothing()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var mutationId = Guid.NewGuid();
        await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0), mutationId));
        var reuse = (await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0.AddHours(1)), mutationId)))[0];
        Assert.Equal("REJECTED", reuse.Status());
        Assert.Equal("VALIDATION_FAILED", reuse.ProblemCode());
        Assert.Equal("REUSED", reuse!["problem"]!["errors"]![0]!["code"]!.GetValue<string>());
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.diaper_event WHERE baby_id = '{baby}'"));
    }

    [Fact]
    public async Task A_deterministic_rejection_is_recorded_and_replays_the_same_rejection()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var id = Guid.NewGuid();
        var mutationId = Guid.NewGuid();
        var bad = Mut.Create(Mut.Diaper, baby, id, new JsonObject { ["occurred_at"] = Mut.At(T0), ["diaper_type"] = "NOPE", ["tz"] = Mut.Tz }, mutationId);
        var first = (await PushOkAsync(ana, bad))[0];
        Assert.Equal("VALIDATION_FAILED", first.ProblemCode());
        Assert.Equal("REJECTED", await AdminScalarAsync<string>($"SELECT outcome FROM nina.sync_mutation WHERE mutation_id = '{mutationId}'"));
        Assert.Equal("VALIDATION_FAILED", await AdminScalarAsync<string>($"SELECT reject_code FROM nina.sync_mutation WHERE mutation_id = '{mutationId}'"));

        // o mesmo mutation_id nunca passa a valer depois: a rejeição se repete (o cliente descarta e gera outro)
        var fixedUp = Mut.Create(Mut.Diaper, baby, id, Mut.DiaperData(T0), mutationId);
        var again = (await PushOkAsync(ana, fixedUp))[0];
        Assert.Equal("REJECTED", again.Status());
        Assert.Equal("VALIDATION_FAILED", again.ProblemCode());
        Assert.Equal(0L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.diaper_event WHERE baby_id = '{baby}'"));
    }

    [Fact]
    public async Task Authorization_rejections_are_not_recorded_so_they_can_be_retried_once_access_changes()
    {
        var ana = await UserAsync();
        var rita = await UserAsync();
        var baby = await BabyAsync(ana);
        var membership = await MemberAsync(baby, rita, "READ_ONLY");
        var mutation = Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0));
        var denied = (await PushOkAsync(rita, mutation))[0];
        Assert.Equal("FORBIDDEN_ROLE", denied.ProblemCode());
        Assert.Equal(0L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.sync_mutation WHERE user_id = '{rita.UserId}'"));

        await AdminExecAsync($"SELECT nina.guard_arm('membership'); UPDATE nina.caregiver_membership SET role = 'CAREGIVER' WHERE id = '{membership}'");
        var allowed = (await PushOkAsync(rita, mutation))[0];
        Assert.Equal("APPLIED", allowed.Status());
    }
}
