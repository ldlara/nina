using System.Text.Json;
using System.Text.Json.Nodes;
using Nina.SyncSpike.Harness;
using Nina.SyncSpike.Server;
using Xunit;

namespace Nina.SyncSpike.Tests;

/// <summary>Um único PostgreSQL temporário para toda a suíte (cada teste usa bebês próprios).</summary>
public sealed class SpikeFixture : IAsyncLifetime
{
    public SpikeEnv Env { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        Env = await SpikeEnv.StartAsync();
        // NINA_SPIKE_TUNED=1: roda a suíte inteira com as políticas RLS por conjunto + índices (spike_tuning.sql)
        if (Environment.GetEnvironmentVariable("NINA_SPIKE_TUNED") == "1") await Env.ApplyTuningAsync();
    }
    public async Task DisposeAsync() => await Env.DisposeAsync();
}

[CollectionDefinition("pg", DisableParallelization = true)]
public sealed class PgCollection : ICollectionFixture<SpikeFixture>;

/// <summary>Cenário padrão: Owner (A) + Caregiver (B) no mesmo bebê, cada um com seu dispositivo.</summary>
public sealed class Trio
{
    public SpikeEnv Env { get; init; } = null!;
    public BabyWorld Baby { get; init; } = null!;
    public Guid UserA { get; init; }
    public Guid UserB { get; init; }
    public FakeDevice A { get; init; } = null!;
    public FakeDevice B { get; init; } = null!;

    public static async Task<Trio> CreateAsync(SpikeEnv env, string roleB = "CAREGIVER")
    {
        var a = await env.CreateUserAsync();
        var b = await env.CreateUserAsync();
        var baby = await env.CreateBabyAsync(a, (b, roleB));
        return new Trio
        {
            Env = env, Baby = baby, UserA = a, UserB = b,
            A = new FakeDevice("A", env.Service, a, baby.BabyId),
            B = new FakeDevice("B", env.Service, b, baby.BabyId),
        };
    }

    public FakeDevice NewDevice(string name, Guid user, SyncService? svc = null) => new(name, svc ?? Env.Service, user, Baby.BabyId);

    public async Task SyncBothTwiceAsync()
    {
        await A.SyncAsync(); await B.SyncAsync(); await A.SyncAsync(); await B.SyncAsync();
    }

    public async Task AssertConvergedAsync(params FakeDevice[] devices)
    {
        var server = await Env.ServerAliveAsync(Baby.BabyId);
        foreach (var d in devices)
        {
            Assert.True(FakeDevice.Equal(d.View(), server, out var diff), $"{d.Name} divergiu do servidor: {diff}");
        }
    }
}

public static class Data
{
    public static readonly DateTimeOffset T0 = new(DateTimeOffset.UtcNow.AddHours(-6).ToUnixTimeSeconds() * TimeSpan.TicksPerSecond + DateTimeOffset.UnixEpoch.Ticks, TimeSpan.Zero);

    public static JsonElement Sleep(DateTimeOffset start, DateTimeOffset? end, string type = "NAP", string? notes = null, string? place = null) =>
        FakeDevice.D(("sleep_type", type), ("start_at", start), ("end_at", end), ("tz", "America/Sao_Paulo"), ("source", "MANUAL"),
            ("notes", notes), ("method_or_place", place));

    public static JsonElement Diaper(DateTimeOffset at, string type = "WET", string? notes = null) =>
        FakeDevice.D(("occurred_at", at), ("tz", "America/Sao_Paulo"), ("diaper_type", type), ("notes", notes));

    public static JsonElement Bottle(DateTimeOffset start, decimal ml = 120) =>
        FakeDevice.D(("feeding_type", "BOTTLE"), ("start_at", start), ("end_at", start.AddMinutes(15)), ("tz", "America/Sao_Paulo"),
            ("volume_ml", ml), ("milk_type", "FORMULA"));

    public static JsonElement Pump(DateTimeOffset start) =>
        FakeDevice.D(("start_at", start), ("end_at", start.AddMinutes(20)), ("tz", "America/Sao_Paulo"), ("volume_ml", 90), ("side", "BOTH"));

    public static JsonElement Wake(Guid session, DateTimeOffset start, DateTimeOffset end, string? source = null) =>
        source == null
            ? FakeDevice.D(("sleep_session_id", session), ("started_at", start), ("ended_at", end))
            : FakeDevice.D(("sleep_session_id", session), ("started_at", start), ("ended_at", end), ("source", source));

    public static string S(JsonObject? o, string key) => o?[key]?.ToString() ?? "<null>";
}
