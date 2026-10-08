using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nina.Tracking.Tests.Infrastructure;

namespace Nina.Tracking.Tests.Tests;

/// <summary>BE-004a: o envelope, a criação de cada entidade, a validação por mutação e a ordem do lote (<c>POST /sync/push</c>).</summary>
public sealed class PushBasicsTests(PostgresFixture postgres) : TrackingTestBase(postgres)
{
    [Fact]
    public async Task Every_entity_type_is_created_and_comes_back_in_the_feed_with_the_contract_shape()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var sleep = Guid.NewGuid();
        var results = await PushOkAsync(
            ana,
            Mut.Create(Mut.Sleep, baby, sleep, Mut.SleepData(T0, T0.AddHours(8), "NIGHT", "TIMER", notes: "dormiu bem")),
            Mut.Create(Mut.Feeding, baby, Guid.NewGuid(), Mut.BreastData(T0.AddHours(9), T0.AddHours(9).AddMinutes(15), "RIGHT")),
            Mut.Create(Mut.Feeding, baby, Guid.NewGuid(), Mut.BottleData(T0.AddHours(10), 150, "BREAST_MILK")),
            Mut.Create(Mut.Feeding, baby, Guid.NewGuid(), new JsonObject { ["feeding_type"] = "SOLID", ["start_at"] = Mut.At(T0.AddHours(11)), ["tz"] = Mut.Tz }),
            Mut.Create(Mut.Pumping, baby, Guid.NewGuid(), Mut.PumpingData(T0.AddHours(12), T0.AddHours(12).AddMinutes(20), 80)),
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0.AddHours(13), "MIXED", "assadura")),
            Mut.Create(Mut.Wake, baby, Guid.NewGuid(), Mut.WakeData(sleep, T0.AddHours(3), T0.AddHours(3).AddMinutes(10), "MANUAL")));

        Assert.All(results, r => Assert.Equal("APPLIED", r.Status()));
        Assert.All(results, r => Assert.Equal("NONE", r.Resolution()));
        Assert.All(results, r => Assert.NotNull(r!["server_received_at"]));

        var (changes, _) = await PullAllAsync(ana, baby);
        var byType = changes.GroupBy(c => c.EntityType()).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(1, byType["BABY"]);
        Assert.Equal(1, byType["SLEEP_SESSION"]);
        Assert.Equal(3, byType["FEEDING_SESSION"]);
        Assert.Equal(1, byType["PUMPING_SESSION"]);
        Assert.Equal(1, byType["DIAPER_EVENT"]);
        Assert.Equal(1, byType["WAKE_EVENT"]);

        var sleepEntity = changes.Single(c => c.EntityType() == "SLEEP_SESSION")["entity"]!;
        Assert.Equal("SLEEP", sleepEntity["event_type"]!.GetValue<string>());
        Assert.Equal(8 * 3600, sleepEntity["duration_seconds"]!.GetValue<long>());
        Assert.Equal("dormiu bem", sleepEntity["notes"]!.GetValue<string>());
        Assert.Equal(1, sleepEntity["night_awakenings"]!.GetValue<int>());          // derivado: um despertar registrado na sessão noturna
        Assert.Equal(ana.UserId.ToString(), sleepEntity["created_by"]!["id"]!.GetValue<string>());
        Assert.Equal("Teste", sleepEntity["created_by"]!["display_name"]!.GetValue<string>());
        var wake = changes.Single(c => c.EntityType() == "WAKE_EVENT")["entity"]!;
        Assert.Equal(600, wake["duration_seconds"]!.GetValue<long>());
        Assert.Equal("MANUAL", wake["source"]!.GetValue<string>());
        var bottle = changes.Select(c => c["entity"]!).Single(e => e["feeding_type"]?.GetValue<string>() == "BOTTLE");
        Assert.Equal(150, bottle["volume_ml"]!.GetValue<long>());
        Assert.Null(bottle["side"]);
        var breast = changes.Select(c => c["entity"]!).Single(e => e["feeding_type"]?.GetValue<string>() == "BREASTFEEDING");
        Assert.Null(breast["milk_type"]);
        Assert.Null(breast["volume_ml"]);
    }

    [Fact]
    public async Task The_version_is_the_per_baby_server_sequence_and_the_feed_returns_the_same_version()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var results = await PushOkAsync(ana, [.. ids.Select((id, i) => Mut.Create(Mut.Diaper, baby, id, Mut.DiaperData(T0.AddMinutes(i))))]);
        var versions = results.Select(r => r.Version()).ToArray();
        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(versions.Length, versions.Distinct().Count());
        var (changes, _) = await PullAllAsync(ana, baby);
        foreach (var (id, version) in ids.Zip(versions))
        {
            Assert.Equal(version, changes.Single(c => c.EntityId() == id)["version"]!.GetValue<long>());
        }
    }

    [Fact]
    public async Task A_rejected_mutation_does_not_block_the_rest_of_the_batch_and_results_keep_the_request_order()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var good1 = Guid.NewGuid();
        var bad = Guid.NewGuid();
        var good2 = Guid.NewGuid();
        var results = await PushOkAsync(
            ana,
            Mut.Create(Mut.Diaper, baby, good1, Mut.DiaperData(T0)),
            Mut.Create(Mut.Feeding, baby, bad, new JsonObject { ["feeding_type"] = "BREASTFEEDING", ["side"] = "LEFT", ["start_at"] = Mut.At(T0), ["tz"] = Mut.Tz }),
            Mut.Create(Mut.Diaper, baby, good2, Mut.DiaperData(T0.AddMinutes(1))));

        Assert.Equal(["APPLIED", "REJECTED", "APPLIED"], results.Select(r => r.Status()).ToArray());
        Assert.Equal("VALIDATION_FAILED", results[1].ProblemCode());
        Assert.False(results[1]!["retryable"]!.GetValue<bool>());
        Assert.Equal("data.end_at", results[1]!["problem"]!["errors"]![0]!["field"]!.GetValue<string>());       // PA-05: end_at obrigatório na mamada
        Assert.Equal(good1.ToString(), results[0]!["entity_id"]!.GetValue<string>());
        Assert.Equal(good2.ToString(), results[2]!["entity_id"]!.GetValue<string>());
        Assert.Equal(2L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.diaper_event WHERE baby_id = '{baby}'"));
        Assert.Equal(0L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.feeding_session WHERE baby_id = '{baby}'"));
    }

    public static TheoryData<string, string, string> InvalidCreations() => new()
    {
        { Mut.Sleep, """{"sleep_type":"NAP","start_at":"2026-01-01T10:00:00Z","tz":"America/Sao_Paulo","source":"MANUAL","bogus":1}""", "data.bogus|UNKNOWN_FIELD" },
        { Mut.Sleep, """{"sleep_type":"DAYDREAM","start_at":"2026-01-01T10:00:00Z","tz":"America/Sao_Paulo","source":"MANUAL"}""", "data.sleep_type|INVALID_VALUE" },
        { Mut.Sleep, """{"sleep_type":"NAP","start_at":"2026-01-01T10:00:00Z","end_at":"2026-01-01T09:00:00Z","tz":"America/Sao_Paulo","source":"MANUAL"}""", "data.end_at|END_BEFORE_START" },
        { Mut.Sleep, """{"sleep_type":"NAP","start_at":"ontem","tz":"America/Sao_Paulo","source":"MANUAL"}""", "data.start_at|INVALID_VALUE" },
        { Mut.Sleep, """{"sleep_type":"NAP","start_at":"2026-01-01T10:00:00Z","tz":"Marte/Olympus","source":"MANUAL"}""", "data.tz|INVALID_VALUE" },
        { Mut.Sleep, """{"sleep_type":"NAP","start_at":"2026-01-01T10:00:00Z","tz":"America/Sao_Paulo"}""", "data.source|REQUIRED" },
        { Mut.Feeding, """{"feeding_type":"BOTTLE","start_at":"2026-01-01T10:00:00Z","tz":"America/Sao_Paulo"}""", "data.volume_ml|REQUIRED" },
        { Mut.Feeding, """{"feeding_type":"BOTTLE","start_at":"2026-01-01T10:00:00Z","volume_ml":0,"tz":"America/Sao_Paulo"}""", "data.volume_ml|OUT_OF_RANGE" },
        { Mut.Feeding, """{"feeding_type":"BOTTLE","start_at":"2026-01-01T10:00:00Z","volume_ml":120.5,"tz":"America/Sao_Paulo"}""", "data.volume_ml|INVALID_TYPE" },
        { Mut.Feeding, """{"feeding_type":"BOTTLE","start_at":"2026-01-01T10:00:00Z","volume_ml":120,"milk_type":"UNKNOWN","tz":"America/Sao_Paulo"}""", "data.milk_type|INVALID_VALUE" },
        { Mut.Feeding, """{"feeding_type":"BREASTFEEDING","side":"LEFT","start_at":"2026-01-01T10:00:00Z","end_at":"2026-01-01T10:10:00Z","milk_type":"FORMULA","tz":"America/Sao_Paulo"}""", "data.milk_type|NOT_APPLICABLE" },
        { Mut.Feeding, """{"feeding_type":"SOLID","start_at":"2026-01-01T10:00:00Z","volume_ml":50,"tz":"America/Sao_Paulo"}""", "data.volume_ml|NOT_APPLICABLE" },
        { Mut.Feeding, """{"feeding_type":"PUREE","start_at":"2026-01-01T10:00:00Z","tz":"America/Sao_Paulo"}""", "data.feeding_type|INVALID_VALUE" },
        { Mut.Pumping, """{"start_at":"2026-01-01T10:00:00Z","tz":"America/Sao_Paulo"}""", "data.end_at|REQUIRED" },
        { Mut.Pumping, """{"start_at":"2026-01-01T10:00:00Z","end_at":"2026-01-01T10:20:00Z","tz":"America/Sao_Paulo","notes":"x"}""", "data.notes|UNKNOWN_FIELD" },
        { Mut.Diaper, """{"occurred_at":"2026-01-01T10:00:00Z","diaper_type":"WET","tz":"America/Sao_Paulo","notes":null}""", string.Empty },
        { Mut.Diaper, """{"occurred_at":"2026-01-01T10:00:00Z","diaper_type":"wet","tz":"America/Sao_Paulo"}""", "data.diaper_type|INVALID_VALUE" },
        { Mut.Diaper, """{"occurred_at":"1999-12-31T23:59:59Z","diaper_type":"WET","tz":"America/Sao_Paulo"}""", "data.occurred_at|OUT_OF_RANGE" },
        { Mut.Wake, """{"started_at":"2026-01-01T10:00:00Z","ended_at":"2026-01-01T10:05:00Z"}""", "data.sleep_session_id|REQUIRED" },
    };

    [Theory]
    [MemberData(nameof(InvalidCreations))]
    public async Task Invalid_data_is_rejected_per_mutation_with_the_failing_field(string type, string json, string expected)
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var data = (JsonObject)JsonNode.Parse(json)!;
        var results = await PushOkAsync(ana, Mut.Create(type, baby, Guid.NewGuid(), data));
        if (expected.Length == 0)
        {
            Assert.Equal("APPLIED", results[0].Status());
            return;
        }

        Assert.Equal("REJECTED", results[0].Status());
        Assert.Equal("VALIDATION_FAILED", results[0].ProblemCode());
        var errors = results[0]!["problem"]!["errors"]!.AsArray().Select(e => e!["field"]!.GetValue<string>() + "|" + e["code"]!.GetValue<string>()).ToArray();
        Assert.Contains(expected, errors);
    }

    [Fact]
    public async Task Text_limits_follow_the_contract_not_the_wider_database_limits()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var results = await PushOkAsync(
            ana,
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0, notes: new string('n', 501))),
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0, notes: new string('n', 500))),
            Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), new JsonObject
            {
                ["sleep_type"] = "NAP", ["start_at"] = Mut.At(T0), ["tz"] = Mut.Tz, ["source"] = "MANUAL", ["method_or_place"] = new string('m', 81),
            }));
        Assert.Equal(["REJECTED", "APPLIED", "REJECTED"], results.Select(r => r.Status()).ToArray());
        Assert.Equal("data.notes", results[0]!["problem"]!["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Equal("data.method_or_place", results[2]!["problem"]!["errors"]![0]!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_update_and_delete_in_one_batch_are_applied_in_order()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var timer = Guid.NewGuid();
        var diaper = Guid.NewGuid();
        var results = await PushOkAsync(
            ana,
            Mut.Create(Mut.Sleep, baby, timer, Mut.SleepData(T0, null, source: "TIMER")),
            Mut.Update(Mut.Sleep, baby, timer, 1, new JsonObject { ["end_at"] = Mut.At(T0.AddMinutes(48)) }),         // base_version chutado: mesma origem, sem conflito
            Mut.Create(Mut.Diaper, baby, diaper, Mut.DiaperData(T0.AddMinutes(50))),
            Mut.Delete(Mut.Diaper, baby, diaper, 1));
        Assert.All(results, r => Assert.Equal("APPLIED", r.Status()));
        Assert.All(results, r => Assert.Equal("NONE", r.Resolution()));
        var versions = results.Select(r => r.Version()).ToArray();
        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(2880L, await AdminScalarAsync<long>($"SELECT extract(epoch FROM end_at - start_at)::bigint FROM nina.sleep_session WHERE id = '{timer}'"));
        Assert.NotNull(await AdminScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.diaper_event WHERE id = '{diaper}'"));
        Assert.Null(await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE id = '{diaper}'"));
    }

    [Fact]
    public async Task An_update_changes_only_the_present_fields_and_null_clears_nullable_ones()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var id = Guid.NewGuid();
        var created = await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, id, Mut.DiaperData(T0, "WET", "primeira nota")));
        var v1 = created[0].Version();
        var edited = await PushOkAsync(ana, Mut.Update(Mut.Diaper, baby, id, v1, new JsonObject { ["diaper_type"] = "DIRTY" }));
        Assert.Equal("APPLIED", edited[0].Status());
        Assert.True(edited[0].Version() > v1);
        Assert.Equal("primeira nota", await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE id = '{id}'"));
        Assert.Equal("DIRTY", await AdminScalarAsync<string>($"SELECT diaper_type FROM nina.diaper_event WHERE id = '{id}'"));

        var cleared = await PushOkAsync(ana, Mut.Update(Mut.Diaper, baby, id, edited[0].Version(), new JsonObject { ["notes"] = null }));
        Assert.Equal("APPLIED", cleared[0].Status());
        Assert.Null(await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE id = '{id}'"));

        // valor igual ao atual: nada a gravar, sem nova versão
        var same = await PushOkAsync(ana, Mut.Update(Mut.Diaper, baby, id, cleared[0].Version(), new JsonObject { ["diaper_type"] = "DIRTY" }));
        Assert.Equal("APPLIED", same[0].Status());
        Assert.Equal(cleared[0].Version(), same[0].Version());
    }

    [Fact]
    public async Task Immutable_and_not_applicable_fields_are_rejected_on_update()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var feeding = Guid.NewGuid();
        var diaper = Guid.NewGuid();
        var created = await PushOkAsync(
            ana,
            Mut.Create(Mut.Feeding, baby, feeding, Mut.BottleData(T0, 100)),
            Mut.Create(Mut.Diaper, baby, diaper, Mut.DiaperData(T0)));
        var results = await PushOkAsync(
            ana,
            Mut.Update(Mut.Feeding, baby, feeding, created[0].Version(), new JsonObject { ["feeding_type"] = "BREASTFEEDING" }),
            Mut.Update(Mut.Feeding, baby, feeding, created[0].Version(), new JsonObject { ["side"] = "LEFT" }),
            Mut.Update(Mut.Diaper, baby, diaper, created[1].Version(), new JsonObject { ["start_at"] = Mut.At(T0) }),
            Mut.Update(Mut.Diaper, baby, diaper, created[1].Version(), new JsonObject()));
        Assert.All(results, r => Assert.Equal("VALIDATION_FAILED", r.ProblemCode()));
        Assert.Equal("data.feeding_type", results[0]!["problem"]!["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Equal("data.side", results[1]!["problem"]!["errors"]![0]!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task Updating_or_deleting_an_unknown_entity_is_not_found_and_a_base_version_ahead_of_the_server_is_refused()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var id = Guid.NewGuid();
        var created = await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, id, Mut.DiaperData(T0)));
        var results = await PushOkAsync(
            ana,
            Mut.Update(Mut.Diaper, baby, Guid.NewGuid(), 1, new JsonObject { ["notes"] = "x" }),
            Mut.Delete(Mut.Diaper, baby, Guid.NewGuid(), 1),
            Mut.Update(Mut.Diaper, baby, id, created[0].Version() + 100, new JsonObject { ["notes"] = "x" }));
        Assert.Equal(["ENTITY_NOT_FOUND", "ENTITY_NOT_FOUND", "VERSION_AHEAD"], results.Select(r => r.ProblemCode()!).ToArray());
        Assert.All(results, r => Assert.False(r!["retryable"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Reusing_an_entity_id_in_the_same_baby_is_unavailable_and_a_tombstoned_id_never_comes_back()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var id = Guid.NewGuid();
        var created = await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, id, Mut.DiaperData(T0)));
        var again = await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, id, Mut.DiaperData(T0.AddHours(1))));
        Assert.Equal("ENTITY_ID_UNAVAILABLE", again[0].ProblemCode());

        await PushOkAsync(ana, Mut.Delete(Mut.Diaper, baby, id, created[0].Version()));
        var recreated = await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, id, Mut.DiaperData(T0.AddHours(2))));
        Assert.Equal("ENTITY_DELETED", recreated[0].ProblemCode());                  // INV-20: sem ressurreição pelo mesmo id
    }

    [Fact]
    public async Task The_envelope_is_validated_and_a_bad_envelope_fails_the_whole_request()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var diaper = Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0));

        var otherDevice = await PushAsync(ana, Guid.NewGuid(), diaper);                 // device_id deve ser o da sessão
        Assert.Equal(HttpStatusCode.BadRequest, otherDevice.Status);
        Assert.Equal("VALIDATION_FAILED", otherDevice.Code);
        Assert.Equal("DEVICE_MISMATCH", otherDevice.FieldErrorCode("device_id"));

        var empty = await PushAsync(ana);
        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);

        var noOp = (JsonObject)diaper.DeepClone();
        noOp["op"] = "UPSERT";
        var badOp = await PushAsync(ana, noOp);
        Assert.Equal(HttpStatusCode.BadRequest, badOp.Status);
        Assert.Equal("INVALID_VALUE", badOp.FieldErrorCode("mutations[0].op"));

        var badId = (JsonObject)diaper.DeepClone();
        badId["entity_id"] = "nao-e-uuid";
        Assert.Equal(HttpStatusCode.BadRequest, (await PushAsync(ana, badId)).Status);

        var tooMany = await PushAsync(ana, [.. Enumerable.Range(0, 101).Select(_ => Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)))]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooMany.Status);
        Assert.Equal("PAYLOAD_TOO_LARGE", tooMany.Code);
        Assert.Equal(0L, await AdminScalarAsync<long>("SELECT count(*) FROM nina.diaper_event"));          // nada foi gravado

        var exactly100 = await PushAsync(ana, [.. Enumerable.Range(0, 100).Select(_ => Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)))]);
        Assert.Equal(HttpStatusCode.OK, exactly100.Status);
        Assert.Equal(100L, await AdminScalarAsync<long>("SELECT count(*) FROM nina.diaper_event"));

        Assert.Equal(HttpStatusCode.BadRequest, (await Api.PostAsync("/v1/sync/push", new JsonObject { ["device_id"] = ana.DeviceId.ToString() }, ana.AccessToken)).Status);
    }

    [Fact]
    public async Task A_body_over_256_KiB_is_413_and_malformed_json_is_400()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var huge = Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0));
        huge["padding"] = new string('x', 300 * 1024);
        var tooLarge = await Api.PostAsync("/v1/sync/push", Mut.Push(ana.DeviceId, huge), ana.AccessToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.Status);
        Assert.Equal("PAYLOAD_TOO_LARGE", tooLarge.Code);

        using var http = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/sync/push") { Content = new StringContent("{ nao é json", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ana.AccessToken);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Push_and_pull_require_authentication()
    {
        var baby = Guid.NewGuid();
        var push = await Api.PostAsync("/v1/sync/push", Mut.Push(Guid.NewGuid(), Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0))));
        Assert.Equal(HttpStatusCode.Unauthorized, push.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.GetAsync($"/v1/sync/pull?baby_id={baby}")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.GetAsync($"/v1/babies/{baby}/timeline")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.GetAsync($"/v1/babies/{baby}/aggregates?period=DAY")).Status);
    }
}
