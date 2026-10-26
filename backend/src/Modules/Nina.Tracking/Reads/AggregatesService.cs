using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.Tracking.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace Nina.Tracking.Reads;

internal sealed record AggregatesQuery(string Period, DateOnly? Anchor, IReadOnlySet<string> Include);

/// <summary>
/// <c>GET /babies/{id}/aggregates</c> (RF-028, RF-029, RF-010). Tudo derivado dos eventos (nada persistido, INV-04), no fuso do evento
/// (<c>tz</c>, RB-014). Decisões onde o produto deixou em aberto (D-13):
/// minutos de sono são repartidos pelo dia civil em que realmente caem (sessão que cruza a meia-noite divide); contagens (sonecas,
/// despertares, mamadas) vão para o dia do início; sessão em aberto não entra nos totais (RF-010-A6); só há bucket para período com dado
/// (sem zeros enganosos, RF-028-A2); a comparação é sempre com o período anterior do mesmo bebê (RB-013).
/// </summary>
internal sealed class AggregatesService(NpgsqlDataSource dataSource)
{
    private const int MinDaysDefault = 3;
    private const double MaxWakeWindowMinutes = 24 * 60;

    private sealed record BabyClock(string Tz, DateOnly Today);

    /// <summary>Acumulado de um bucket (dia ou hora).</summary>
    private sealed class Bucket
    {
        public double SleepMinutes;
        public double NightMinutes;
        public double NapMinutes;
        public int NapCount;
        public bool HasSleep;
        public int? NightAwakenings;
        public List<double> WakeWindows { get; } = [];
        public int BreastCount;
        public double BreastMinutes;
        public int BottleCount;
        public double BottleVolume;
        public bool HasFeeding;
        public int PumpCount;
        public double PumpMinutes;
        public double PumpVolume;
        public bool HasPumping;
        public int DiaperCount;
        public SortedDictionary<string, int> DiaperTypes { get; } = new(StringComparer.Ordinal);
    }

    public async Task<JsonObject> GetAsync(Guid userId, Guid babyId, AggregatesQuery q, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await TrackingTx.BeginAsync(dataSource, userId, null, IsolationLevel.RepeatableRead, ct);
        var access = await BabyAccess.ResolveAsync(tx, babyId);
        access.RequireRead();

        var baby = await tx.QueryFirstAsync(
            "SELECT timezone, ((now() AT TIME ZONE timezone)::date)::text FROM nina.baby WHERE id = @baby",
            r => new BabyClock(r.GetString(0), DateOnly.Parse(r.GetString(1), CultureInfo.InvariantCulture)),
            Db.Uuid("baby", babyId)) ?? throw ProblemException.NotFound();
        var anchor = q.Anchor ?? baby.Today;
        var (from, to, prevFrom, prevTo) = Ranges(q.Period, anchor);
        var hourly = q.Period == "DAY";

        var daily = await CollectAsync(tx, babyId, q.Include, prevFrom, to, hourly: false);
        var current = daily.Where(kv => InRange(kv.Key, from, to)).ToList();
        var previous = daily.Where(kv => InRange(kv.Key, prevFrom, prevTo)).ToList();

        var buckets = new JsonArray();
        var shown = hourly ? await CollectAsync(tx, babyId, q.Include, from, to, hourly: true) : null;
        await AddBucketsAsync(tx, buckets, baby.Tz, (shown ?? new SortedDictionary<DateTime, Bucket>(current.ToDictionary(kv => kv.Key, kv => kv.Value))).Select(kv => (kv.Key, kv.Value)), q.Include);

        var summary = Summarize(current.Select(kv => kv.Value), q.Include);
        var prevSummary = Summarize(previous.Select(kv => kv.Value), q.Include);
        var minDays = q.Period == "DAY" ? 1 : await tx.ScalarAsync<int>("SELECT nina.param_int('limits.aggregates_min_days_required', @def)", Db.P("def", MinDaysDefault, NpgsqlDbType.Integer));
        var daysWithData = (int)summary["days_counted"]!.GetValue<long>();

        JsonNode? comparison = null;
        if (previous.Count > 0)
        {
            comparison = new JsonObject
            {
                ["kind"] = "OWN_HISTORY",
                ["previous_range"] = new JsonObject { ["from"] = ReadService.Format(prevFrom), ["to"] = ReadService.Format(prevTo) },
                ["deltas"] = Deltas(summary, prevSummary),
            };
        }

        return new JsonObject
        {
            ["baby_id"] = babyId.ToString("D"),
            ["period"] = q.Period,
            ["range"] = new JsonObject { ["from"] = ReadService.Format(from), ["to"] = ReadService.Format(to) },
            ["tz"] = baby.Tz,
            ["granularity"] = hourly ? "HOUR" : "DAY",
            ["generated_at"] = Domain.Wire.Instant(now),
            ["buckets"] = buckets,
            ["summary"] = summary,
            ["comparison"] = comparison,
            ["data_sufficiency"] = new JsonObject
            {
                ["sufficient"] = daysWithData >= minDays && daysWithData > 0,
                ["days_with_data"] = daysWithData,
                ["min_days_required"] = minDays,
            },
        };
    }

    // ---------------------------------------------------------------- períodos

    private static (DateOnly From, DateOnly To, DateOnly PrevFrom, DateOnly PrevTo) Ranges(string period, DateOnly anchor)
    {
        switch (period)
        {
            case "DAY":
                return (anchor, anchor, anchor.AddDays(-1), anchor.AddDays(-1));
            case "WEEK":
                var monday = anchor.AddDays(-(((int)anchor.DayOfWeek + 6) % 7));
                return (monday, monday.AddDays(6), monday.AddDays(-7), monday.AddDays(-1));
            default:
                var first = new DateOnly(anchor.Year, anchor.Month, 1);
                var last = first.AddMonths(1).AddDays(-1);
                var prevFirst = first.AddMonths(-1);
                return (first, last, prevFirst, first.AddDays(-1));
        }
    }

    // ---------------------------------------------------------------- coleta

    /// <summary>Acumula por dia civil (ou, no gráfico do dia, por hora; a chave é o início do bucket em hora local).</summary>
    private static async Task<SortedDictionary<DateTime, Bucket>> CollectAsync(
        TrackingTx tx, Guid babyId, IReadOnlySet<string> include, DateOnly from, DateOnly to, bool hourly)
    {
        var result = new SortedDictionary<DateTime, Bucket>();
        Bucket Get(DateTime localStart)
        {
            if (!result.TryGetValue(localStart, out var bucket))
            {
                result[localStart] = bucket = new Bucket();
            }

            return bucket;
        }

        // Janela em UTC larga o bastante para qualquer fuso (o filtro fino é pelo dia/hora local da linha).
        var lo = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var hi = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(2);
        var common = new[]
        {
            Db.Uuid("baby", babyId), Db.Timestamp("lo", lo), Db.Timestamp("hi", hi),
            Db.P("from", from.ToDateTime(TimeOnly.MinValue), NpgsqlDbType.Timestamp),
            Db.P("to", to.AddDays(1).ToDateTime(TimeOnly.MinValue), NpgsqlDbType.Timestamp),
        };
        var step = hourly ? "hour" : "day";
        var cast = hourly ? "date_trunc('hour', {0})" : "date_trunc('day', {0})";

        if (include.Contains("SLEEP"))
        {
            var rows = await tx.QueryAsync(
                $"""
                WITH s AS (
                  SELECT id, sleep_type, start_at, end_at, tz FROM nina.sleep_session
                   WHERE baby_id = @baby AND deleted_at IS NULL AND end_at IS NOT NULL AND end_at > @lo AND start_at < @hi),
                d AS (
                  SELECT s.*, gs AS bucket_start
                    FROM s, LATERAL generate_series({string.Format(CultureInfo.InvariantCulture, cast, "(s.start_at AT TIME ZONE s.tz)")},
                                                    (s.end_at AT TIME ZONE s.tz), interval '1 {step}') gs)
                SELECT bucket_start, sleep_type,
                       extract(epoch FROM (least(end_at, ((bucket_start + interval '1 {step}') AT TIME ZONE tz))
                                           - greatest(start_at, (bucket_start AT TIME ZONE tz)))) / 60.0,
                       {string.Format(CultureInfo.InvariantCulture, cast, "(start_at AT TIME ZONE tz)")} = bucket_start,
                       CASE WHEN sleep_type = 'NIGHT' AND {string.Format(CultureInfo.InvariantCulture, cast, "(start_at AT TIME ZONE tz)")} = bucket_start
                            THEN nina.night_awakenings(@baby, id) END,
                       sleep_type = 'NIGHT' AND {string.Format(CultureInfo.InvariantCulture, cast, "(start_at AT TIME ZONE tz)")} = bucket_start
                  FROM d
                 WHERE bucket_start >= @from AND bucket_start < @to
                """,
                r => (Start: r.GetDateTime(0), Type: r.GetString(1), Minutes: Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture),
                      IsStart: r.GetBoolean(3), Nights: r.IsDBNull(4) ? (int?)null : r.GetInt32(4), IsNightStart: r.GetBoolean(5)),
                [.. common]);
            foreach (var row in rows)
            {
                var b = Get(row.Start);
                b.HasSleep = true;
                b.SleepMinutes += row.Minutes;
                if (row.Type == "NIGHT")
                {
                    b.NightMinutes += row.Minutes;
                }
                else
                {
                    b.NapMinutes += row.Minutes;
                }

                if (row.IsStart && row.Type == "NAP")
                {
                    b.NapCount++;
                }

                if (row.IsNightStart && row.Nights is { } n)
                {
                    b.NightAwakenings = (b.NightAwakenings ?? 0) + n;
                }
            }

            if (!hourly)
            {
                await AddWakeWindowsAsync(tx, common, Get, from, to);
            }
        }

        if (include.Contains("FEEDING"))
        {
            var rows = await tx.QueryAsync(
                $"""
                SELECT {string.Format(CultureInfo.InvariantCulture, cast, "(start_at AT TIME ZONE tz)")}, feeding_type, count(*),
                       coalesce(sum(extract(epoch FROM (end_at - start_at)) / 60.0), 0), coalesce(sum(volume_ml), 0)
                  FROM nina.feeding_session
                 WHERE baby_id = @baby AND deleted_at IS NULL AND start_at >= @lo AND start_at < @hi
                   AND (start_at AT TIME ZONE tz) >= @from AND (start_at AT TIME ZONE tz) < @to
                 GROUP BY 1, 2
                """,
                r => (Start: r.GetDateTime(0), Type: r.GetString(1), Count: (int)r.GetInt64(2),
                      Minutes: Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture), Volume: Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture)),
                [.. common]);
            foreach (var row in rows)
            {
                var b = Get(row.Start);
                if (row.Type == "BREASTFEEDING")
                {
                    b.HasFeeding = true;
                    b.BreastCount += row.Count;
                    b.BreastMinutes += row.Minutes;
                }
                else if (row.Type == "BOTTLE")
                {
                    b.HasFeeding = true;
                    b.BottleCount += row.Count;
                    b.BottleVolume += row.Volume;
                }
            }
        }

        if (include.Contains("PUMPING"))
        {
            var rows = await tx.QueryAsync(
                $"""
                SELECT {string.Format(CultureInfo.InvariantCulture, cast, "(start_at AT TIME ZONE tz)")}, count(*),
                       coalesce(sum(extract(epoch FROM (end_at - start_at)) / 60.0), 0), coalesce(sum(volume_ml), 0)
                  FROM nina.pumping_session
                 WHERE baby_id = @baby AND deleted_at IS NULL AND start_at >= @lo AND start_at < @hi
                   AND (start_at AT TIME ZONE tz) >= @from AND (start_at AT TIME ZONE tz) < @to
                 GROUP BY 1
                """,
                r => (Start: r.GetDateTime(0), Count: (int)r.GetInt64(1),
                      Minutes: Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture), Volume: Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture)),
                [.. common]);
            foreach (var row in rows)
            {
                var b = Get(row.Start);
                b.HasPumping = true;
                b.PumpCount += row.Count;
                b.PumpMinutes += row.Minutes;
                b.PumpVolume += row.Volume;
            }
        }

        if (include.Contains("DIAPERS"))
        {
            var rows = await tx.QueryAsync(
                $"""
                SELECT {string.Format(CultureInfo.InvariantCulture, cast, "(occurred_at AT TIME ZONE tz)")}, diaper_type, count(*)
                  FROM nina.diaper_event
                 WHERE baby_id = @baby AND deleted_at IS NULL AND occurred_at >= @lo AND occurred_at < @hi
                   AND (occurred_at AT TIME ZONE tz) >= @from AND (occurred_at AT TIME ZONE tz) < @to
                 GROUP BY 1, 2
                """,
                r => (Start: r.GetDateTime(0), Type: r.GetString(1), Count: (int)r.GetInt64(2)),
                [.. common]);
            foreach (var row in rows)
            {
                var b = Get(row.Start);
                b.DiaperCount += row.Count;
                b.DiaperTypes[row.Type] = b.DiaperTypes.GetValueOrDefault(row.Type) + row.Count;
            }
        }

        return result;
    }

    private static async Task AddWakeWindowsAsync(
        TrackingTx tx, NpgsqlParameter[] common, Func<DateTime, Bucket> get, DateOnly from, DateOnly to)
    {
        var sessions = await tx.QueryAsync(
            """
            SELECT start_at, end_at, date_trunc('day', (start_at AT TIME ZONE tz))
              FROM nina.sleep_session
             WHERE baby_id = @baby AND deleted_at IS NULL AND end_at IS NOT NULL AND start_at >= @lo AND start_at < @hi
             ORDER BY start_at, id
            """,
            r => (Start: r.GetFieldValue<DateTimeOffset>(0), End: r.GetFieldValue<DateTimeOffset>(1), Day: r.GetDateTime(2)),
            [.. common]);
        for (var i = 1; i < sessions.Count; i++)
        {
            var gap = (sessions[i].Start - sessions[i - 1].End).TotalMinutes;
            var day = DateOnly.FromDateTime(sessions[i].Day);
            if (gap >= 0 && gap <= MaxWakeWindowMinutes && InRange(sessions[i].Day, from, to))
            {
                get(sessions[i].Day).WakeWindows.Add(gap);
            }
        }
    }

    // ---------------------------------------------------------------- saída

    private static async Task AddBucketsAsync(
        TrackingTx tx, JsonArray buckets, string tz, IEnumerable<(DateTime LocalStart, Bucket Bucket)> items, IReadOnlySet<string> include)
    {
        var ordered = items.OrderBy(i => i.LocalStart).ToList();
        if (ordered.Count == 0)
        {
            return;
        }

        var starts = await tx.QueryAsync(
            "SELECT t, (t AT TIME ZONE @tz) FROM unnest(@locals) AS t",
            r => (Local: r.GetDateTime(0), Utc: r.GetFieldValue<DateTimeOffset>(1)),
            Db.Text("tz", tz), new NpgsqlParameter("locals", NpgsqlDbType.Array | NpgsqlDbType.Timestamp) { Value = ordered.Select(o => o.Item1).ToArray() });
        var utc = starts.ToDictionary(s => s.Local, s => s.Utc);
        foreach (var (local, b) in ordered)
        {
            var dto = new JsonObject
            {
                ["local_date"] = ReadService.Format(DateOnly.FromDateTime(local)),
                ["start_at"] = Domain.Wire.Instant(utc[local]),
            };
            if (include.Contains("SLEEP"))
            {
                dto["sleep"] = new JsonObject
                {
                    ["total_minutes"] = (long)Math.Round(b.SleepMinutes),
                    ["night_minutes"] = (long)Math.Round(b.NightMinutes),
                    ["nap_minutes"] = (long)Math.Round(b.NapMinutes),
                    ["nap_count"] = b.NapCount,
                    ["night_awakenings"] = b.NightAwakenings,
                    ["wake_window_minutes"] = b.WakeWindows.Count == 0
                        ? null
                        : new JsonObject { ["avg"] = Round(b.WakeWindows.Average()), ["min"] = Round(b.WakeWindows.Min()), ["max"] = Round(b.WakeWindows.Max()) },
                };
            }

            if (include.Contains("FEEDING"))
            {
                dto["feeding"] = new JsonObject
                {
                    ["breast_count"] = b.BreastCount,
                    ["breast_minutes"] = (long)Math.Round(b.BreastMinutes),
                    ["bottle_count"] = b.BottleCount,
                    ["bottle_volume_ml"] = (long)Math.Round(b.BottleVolume),
                };
            }

            if (include.Contains("PUMPING"))
            {
                dto["pumping"] = new JsonObject
                {
                    ["count"] = b.PumpCount,
                    ["minutes"] = (long)Math.Round(b.PumpMinutes),
                    ["volume_ml"] = (long)Math.Round(b.PumpVolume),
                };
            }

            if (include.Contains("DIAPERS"))
            {
                var byType = new JsonObject();
                foreach (var (type, count) in b.DiaperTypes)
                {
                    byType[type] = count;
                }

                dto["diapers"] = new JsonObject { ["count"] = b.DiaperCount, ["by_type"] = byType };
            }

            buckets.Add(dto);
        }
    }

    private static bool InRange(DateTime local, DateOnly from, DateOnly to) =>
        DateOnly.FromDateTime(local) >= from && DateOnly.FromDateTime(local) <= to;

    private static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static JsonObject Summarize(IEnumerable<Bucket> days, IReadOnlySet<string> include)
    {
        var list = days.ToList();
        double? Avg(IEnumerable<double> values)
        {
            var v = values.ToList();
            return v.Count == 0 ? null : Math.Round(v.Average(), 2, MidpointRounding.AwayFromZero);
        }

        var sleepDays = list.Where(d => d.HasSleep).ToList();
        var feedingDays = list.Where(d => d.HasFeeding).ToList();
        return new JsonObject
        {
            ["days_counted"] = (long)list.Count,
            ["sleep_total_minutes_avg"] = include.Contains("SLEEP") ? Avg(sleepDays.Select(d => d.SleepMinutes)) : null,
            ["nap_count_avg"] = include.Contains("SLEEP") ? Avg(sleepDays.Select(d => (double)d.NapCount)) : null,
            ["night_awakenings_avg"] = include.Contains("SLEEP") ? Avg(list.Where(d => d.NightAwakenings is not null).Select(d => (double)d.NightAwakenings!.Value)) : null,
            ["feeding_count_avg"] = include.Contains("FEEDING") ? Avg(feedingDays.Select(d => (double)(d.BreastCount + d.BottleCount))) : null,
            ["bottle_volume_ml_avg"] = include.Contains("FEEDING") ? Avg(list.Where(d => d.BottleCount > 0).Select(d => d.BottleVolume)) : null,
            ["pumping_volume_ml_avg"] = include.Contains("PUMPING") ? Avg(list.Where(d => d.HasPumping).Select(d => d.PumpVolume)) : null,
            ["diaper_count_avg"] = include.Contains("DIAPERS") ? Avg(list.Where(d => d.DiaperCount > 0).Select(d => (double)d.DiaperCount)) : null,
        };
    }

    private static JsonObject Deltas(JsonObject current, JsonObject previous)
    {
        var deltas = new JsonObject();
        foreach (var (key, value) in current)
        {
            if (key == "days_counted")
            {
                deltas[key] = current[key]!.GetValue<long>() - previous[key]!.GetValue<long>();
                continue;
            }

            deltas[key] = value is not null && previous[key] is not null
                ? Math.Round(value.GetValue<double>() - previous[key]!.GetValue<double>(), 2, MidpointRounding.AwayFromZero)
                : null;
        }

        return deltas;
    }
}
