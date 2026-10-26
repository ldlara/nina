using System.Net;
using System.Text.Json.Nodes;
using Nina.Tracking.Tests.Infrastructure;

namespace Nina.Tracking.Tests.Tests;

/// <summary>RF-019/020/028/029/010: timeline, evento, histórico e agregações (derivadas, ADR-0009).</summary>
public sealed class ReadTests(PostgresFixture postgres) : TrackingTestBase(postgres)
{
    private static string D(string iso) => iso;

    private static DateTimeOffset Z(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();

    [Fact]
    public async Task The_timeline_lists_open_sessions_first_then_newest_first_pages_by_token_and_hides_tombstones()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var open = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        var created = await PushOkAsync(
            ana,
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0.AddHours(1))),
            Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(T0.AddHours(2), T0.AddHours(3))),
            Mut.Create(Mut.Sleep, baby, open, Mut.SleepData(T0.AddHours(-5), null, source: "TIMER")),
            Mut.Create(Mut.Feeding, baby, Guid.NewGuid(), Mut.BottleData(T0.AddHours(4))),
            Mut.Create(Mut.Pumping, baby, Guid.NewGuid(), Mut.PumpingData(T0.AddHours(5), T0.AddHours(5).AddMinutes(20))),
            Mut.Create(Mut.Diaper, baby, deleted, Mut.DiaperData(T0.AddHours(9))));
        await PushOkAsync(ana, Mut.Delete(Mut.Diaper, baby, deleted, created[5].Version()));

        var seen = new List<JsonNode>();
        string? token = null;
        var pages = 0;
        do
        {
            var page = await Api.GetAsync($"/v1/babies/{baby}/timeline?limit=2" + (token is null ? string.Empty : "&page_token=" + Uri.EscapeDataString(token)), ana.AccessToken);
            Assert.Equal(HttpStatusCode.OK, page.Status);
            OpenApiContract.AssertValid("TimelinePage", page.Json);
            seen.AddRange(page.Json!["items"]!.AsArray().Select(i => i!.DeepClone()));
            token = page.Json["page"]!["next_page_token"]?.GetValue<string>();
            pages++;
        }
        while (token is not null);

        Assert.Equal(3, pages);
        Assert.Equal(5, seen.Count);
        Assert.Equal(open.ToString(), seen[0]["id"]!.GetValue<string>());                         // em andamento no topo (RF-019-A7)
        var times = seen.Skip(1).Select(i => (i["start_at"] ?? i["occurred_at"])!.GetValue<string>()).ToArray();
        Assert.Equal(times.OrderByDescending(x => x).ToArray(), times);
        Assert.DoesNotContain(seen, i => i["id"]!.GetValue<string>() == deleted.ToString());
        Assert.Equal(5, seen.Select(i => i["id"]!.GetValue<string>()).Distinct().Count());
    }

    [Fact]
    public async Task The_timeline_filters_by_type_window_and_open_flag_and_rejects_bad_parameters()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        await PushOkAsync(
            ana,
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)),
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0.AddHours(5))),
            Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(T0.AddHours(1), null, source: "TIMER")),
            Mut.Create(Mut.Feeding, baby, Guid.NewGuid(), Mut.BottleData(T0.AddHours(2))));

        async Task<JsonArray> Items(string query) => (await Api.GetAsync($"/v1/babies/{baby}/timeline?{query}", ana.AccessToken)).Json!["items"]!.AsArray();
        Assert.Equal(2, (await Items("types=DIAPER")).Count);
        Assert.Equal(3, (await Items("types=DIAPER,FEEDING")).Count);
        Assert.Equal(3, (await Items("include_open=false")).Count);
        Assert.Single(await Items($"types=DIAPER&from={Uri.EscapeDataString(Mut.At(T0.AddHours(2)))}"));
        Assert.Single(await Items($"types=DIAPER&to={Uri.EscapeDataString(Mut.At(T0.AddHours(2)))}"));
        foreach (var bad in new[] { "limit=0", "limit=101", "types=COOKING", "from=ontem", "include_open=talvez", "page_token=adulterado" })
        {
            var response = await Api.GetAsync($"/v1/babies/{baby}/timeline?{bad}", ana.AccessToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.Status);
            Assert.Equal("VALIDATION_FAILED", response.Code);
        }
    }

    [Fact]
    public async Task GET_event_returns_the_contract_event_and_deleted_or_wake_events_are_404()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var sleep = Guid.NewGuid();
        var wake = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var created = await PushOkAsync(
            ana,
            Mut.Create(Mut.Sleep, baby, sleep, Mut.SleepData(T0, T0.AddHours(6), "NIGHT")),
            Mut.Create(Mut.Wake, baby, wake, Mut.WakeData(sleep, T0.AddHours(2), T0.AddHours(2).AddMinutes(4))),
            Mut.Create(Mut.Feeding, baby, Guid.NewGuid(), Mut.BreastData(T0, T0.AddMinutes(10))),
            Mut.Create(Mut.Diaper, baby, gone, Mut.DiaperData(T0)));
        await PushOkAsync(ana, Mut.Delete(Mut.Diaper, baby, gone, created[3].Version()));

        var response = await Api.GetAsync($"/v1/babies/{baby}/events/{sleep}", ana.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        OpenApiContract.AssertValid("Event", response.Json);
        Assert.Equal(1, response.Json!["night_awakenings"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync($"/v1/babies/{baby}/events/{gone}", ana.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync($"/v1/babies/{baby}/events/{wake}", ana.AccessToken)).Status);   // despertar não é Event
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync($"/v1/babies/{baby}/events/{Guid.NewGuid()}", ana.AccessToken)).Status);
    }

    [Fact]
    public async Task The_event_history_names_who_changed_it_and_when_without_content()
    {
        var ana = await UserAsync();
        var bia = await UserAsync();
        var baby = await BabyAsync(ana);
        await MemberAsync(baby, bia, "CAREGIVER");
        var id = Guid.NewGuid();
        var created = (await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, id, Mut.DiaperData(T0, notes: "privado"))))[0];
        await PushOkAsync(bia, Mut.Update(Mut.Diaper, baby, id, created.Version(), new JsonObject { ["diaper_type"] = "DIRTY" }));
        var response = await Api.GetAsync($"/v1/babies/{baby}/events/{id}/history", ana.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        var items = response.Json!["items"]!.AsArray();
        Assert.Equal(["UPDATED", "CREATED"], items.Select(i => i!["action"]!.GetValue<string>()).ToArray());
        Assert.Equal(bia.UserId.ToString(), items[0]!["actor"]!["id"]!.GetValue<string>());
        Assert.DoesNotContain("privado", response.Json.ToJsonString(), StringComparison.Ordinal);
        foreach (var item in items)
        {
            OpenApiContract.AssertValid("EventHistoryEntry", item);
        }
    }

    private async Task<(Session Ana, Guid Baby)> SeedWeekAsync()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);        // America/Sao_Paulo, UTC-3 o ano todo
        var night = Guid.NewGuid();
        await PushOkAsync(
            ana,
            // noite de 09/03 22:00 a 10/03 06:00 locais (cruza a meia-noite) com 1 despertar
            Mut.Create(Mut.Sleep, baby, night, Mut.SleepData(Z("2026-03-10T01:00:00Z"), Z("2026-03-10T09:00:00Z"), "NIGHT")),
            Mut.Create(Mut.Wake, baby, Guid.NewGuid(), Mut.WakeData(night, Z("2026-03-10T04:00:00Z"), Z("2026-03-10T04:10:00Z"))),
            Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(Z("2026-03-10T12:00:00Z"), Z("2026-03-10T13:00:00Z"))),
            Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(Z("2026-03-10T17:00:00Z"), Z("2026-03-10T17:30:00Z"))),
            Mut.Create(Mut.Feeding, baby, Guid.NewGuid(), Mut.BreastData(Z("2026-03-10T11:00:00Z"), Z("2026-03-10T11:15:00Z"))),
            Mut.Create(Mut.Feeding, baby, Guid.NewGuid(), Mut.BottleData(Z("2026-03-10T15:00:00Z"), 120)),
            Mut.Create(Mut.Feeding, baby, Guid.NewGuid(), Mut.BottleData(Z("2026-03-11T15:00:00Z"), 100)),
            Mut.Create(Mut.Pumping, baby, Guid.NewGuid(), Mut.PumpingData(Z("2026-03-10T18:00:00Z"), Z("2026-03-10T18:20:00Z"), 80)),
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(Z("2026-03-10T12:00:00Z"), "WET")),
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(Z("2026-03-10T13:00:00Z"), "WET")),
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(Z("2026-03-10T14:00:00Z"), "DIRTY")),
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(Z("2026-03-11T14:00:00Z"), "WET")),
            Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(Z("2026-03-12T12:00:00Z"), null, source: "TIMER")));   // em aberto: fora dos totais
        return (ana, baby);
    }

    [Fact]
    public async Task Weekly_aggregates_split_sleep_across_midnight_derive_awakenings_and_conform_to_the_contract()
    {
        var (ana, baby) = await SeedWeekAsync();
        var response = await Api.GetAsync($"/v1/babies/{baby}/aggregates?period=WEEK&anchor_date=2026-03-11", ana.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        OpenApiContract.AssertValid("AggregatesResponse", response.Json);
        var json = response.Json!;
        Assert.Equal("2026-03-09", json["range"]!["from"]!.GetValue<string>());
        Assert.Equal("2026-03-15", json["range"]!["to"]!.GetValue<string>());
        Assert.Equal("DAY", json["granularity"]!.GetValue<string>());
        Assert.Equal("America/Sao_Paulo", json["tz"]!.GetValue<string>());

        var buckets = json["buckets"]!.AsArray().ToDictionary(b => b!["local_date"]!.GetValue<string>(), b => b!);
        Assert.Equal(["2026-03-09", "2026-03-10", "2026-03-11"], buckets.Keys.Order().ToArray());      // só dias com dado; o timer aberto de 12/03 não conta
        Assert.Equal("2026-03-09T03:00:00Z", buckets["2026-03-09"]["start_at"]!.GetValue<string>());
        var d9 = buckets["2026-03-09"]["sleep"]!;
        Assert.Equal(120, d9["total_minutes"]!.GetValue<long>());                                      // 22:00-24:00 da noite que cruza a meia-noite
        Assert.Equal(1, d9["night_awakenings"]!.GetValue<int>());                                      // contagem vai ao dia do início
        var d10 = buckets["2026-03-10"];
        Assert.Equal(450, d10["sleep"]!["total_minutes"]!.GetValue<long>());                           // 360 da noite + 90 de sonecas
        Assert.Equal(360, d10["sleep"]!["night_minutes"]!.GetValue<long>());
        Assert.Equal(2, d10["sleep"]!["nap_count"]!.GetValue<int>());
        Assert.Null(d10["sleep"]!["night_awakenings"]);                                                // nenhuma noite começou em 10/03: sem dado
        Assert.Equal(210, d10["sleep"]!["wake_window_minutes"]!["avg"]!.GetValue<double>());
        Assert.Equal(180, d10["sleep"]!["wake_window_minutes"]!["min"]!.GetValue<double>());
        Assert.Equal(1, d10["feeding"]!["breast_count"]!.GetValue<int>());
        Assert.Equal(15, d10["feeding"]!["breast_minutes"]!.GetValue<long>());
        Assert.Equal(120, d10["feeding"]!["bottle_volume_ml"]!.GetValue<long>());
        Assert.Equal(80, d10["pumping"]!["volume_ml"]!.GetValue<long>());
        Assert.Equal(3, d10["diapers"]!["count"]!.GetValue<int>());
        Assert.Equal(2, d10["diapers"]!["by_type"]!["WET"]!.GetValue<int>());

        var summary = json["summary"]!;
        Assert.Equal(3, summary["days_counted"]!.GetValue<int>());
        Assert.Equal(285, summary["sleep_total_minutes_avg"]!.GetValue<double>());
        Assert.Equal(1, summary["night_awakenings_avg"]!.GetValue<double>());
        Assert.Equal(1.5, summary["feeding_count_avg"]!.GetValue<double>());
        Assert.Equal(110, summary["bottle_volume_ml_avg"]!.GetValue<double>());
        Assert.Equal(2, summary["diaper_count_avg"]!.GetValue<double>());
        Assert.Null(json["comparison"]);                                                               // sem semana anterior
        Assert.True(json["data_sufficiency"]!["sufficient"]!.GetValue<bool>());
        Assert.Equal(3, json["data_sufficiency"]!["days_with_data"]!.GetValue<int>());
    }

    [Fact]
    public async Task Aggregates_compare_only_with_the_babys_own_previous_period_by_day_hour_and_month()
    {
        var (ana, baby) = await SeedWeekAsync();
        await PushOkAsync(ana, Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(Z("2026-03-03T12:00:00Z"), Z("2026-03-03T13:00:00Z"))));      // semana anterior: 60 min

        var week = (await Api.GetAsync($"/v1/babies/{baby}/aggregates?period=WEEK&anchor_date=2026-03-11&include=SLEEP", ana.AccessToken)).Json!;
        OpenApiContract.AssertValid("AggregatesResponse", week);
        Assert.Equal("OWN_HISTORY", week["comparison"]!["kind"]!.GetValue<string>());
        Assert.Equal("2026-03-02", week["comparison"]!["previous_range"]!["from"]!.GetValue<string>());
        Assert.Equal(225, week["comparison"]!["deltas"]!["sleep_total_minutes_avg"]!.GetValue<double>());      // 285 - 60
        Assert.Null(week["summary"]!["feeding_count_avg"]);                                                    // domínio não pedido
        Assert.Null(week["buckets"]![0]!["feeding"]);

        var day = (await Api.GetAsync($"/v1/babies/{baby}/aggregates?period=DAY&anchor_date=2026-03-10", ana.AccessToken)).Json!;
        OpenApiContract.AssertValid("AggregatesResponse", day);
        Assert.Equal("HOUR", day["granularity"]!.GetValue<string>());
        var hours = day["buckets"]!.AsArray();
        Assert.Equal(60, hours.First(b => b!["start_at"]!.GetValue<string>() == "2026-03-10T03:00:00Z")!["sleep"]!["total_minutes"]!.GetValue<long>());      // 00:00 local
        Assert.True(hours.Count > 3);

        var month = (await Api.GetAsync($"/v1/babies/{baby}/aggregates?period=MONTH&anchor_date=2026-03-20", ana.AccessToken)).Json!;
        OpenApiContract.AssertValid("AggregatesResponse", month);
        Assert.Equal("2026-03-01", month["range"]!["from"]!.GetValue<string>());
        Assert.Equal("2026-03-31", month["range"]!["to"]!.GetValue<string>());
        Assert.Null(month["comparison"]);                                                                      // fevereiro sem dado
    }

    [Fact]
    public async Task A_period_without_data_has_empty_buckets_and_is_not_sufficient_and_bad_parameters_are_400()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var empty = (await Api.GetAsync($"/v1/babies/{baby}/aggregates?period=WEEK&anchor_date=2026-03-11", ana.AccessToken)).Json!;
        OpenApiContract.AssertValid("AggregatesResponse", empty);
        Assert.Empty(empty["buckets"]!.AsArray());
        Assert.False(empty["data_sufficiency"]!["sufficient"]!.GetValue<bool>());
        Assert.Null(empty["summary"]!["sleep_total_minutes_avg"]);                                             // nulo, nunca zero enganoso
        Assert.Null(empty["summary"]!["night_awakenings_avg"]);
        foreach (var bad in new[] { string.Empty, "period=YEAR", "period=DAY&anchor_date=11/03/2026", "period=DAY&include=SLEEP,NAPS" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Api.GetAsync($"/v1/babies/{baby}/aggregates?{bad}", ana.AccessToken)).Status);
        }
    }

    [Fact]
    public async Task Night_awakenings_in_aggregates_is_zero_with_enough_follow_up_and_null_otherwise()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        await PushOkAsync(
            ana,
            Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(Z("2026-03-10T01:00:00Z"), Z("2026-03-10T09:00:00Z"), "NIGHT")),        // 8 h, sem despertar: 0
            Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(Z("2026-03-11T01:00:00Z"), Z("2026-03-11T03:00:00Z"), "NIGHT")));       // 2 h: insuficiente
        var json = (await Api.GetAsync($"/v1/babies/{baby}/aggregates?period=WEEK&anchor_date=2026-03-10&include=SLEEP", ana.AccessToken)).Json!;
        var byDate = json["buckets"]!.AsArray().ToDictionary(b => b!["local_date"]!.GetValue<string>(), b => b!["sleep"]!["night_awakenings"]);
        Assert.Equal(0, byDate["2026-03-09"]!.GetValue<int>());
        Assert.Null(byDate["2026-03-10"]);
        Assert.Equal(0, json["summary"]!["night_awakenings_avg"]!.GetValue<double>());
    }
}
