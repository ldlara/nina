using Nina.SyncSpike.Harness;
using Nina.SyncSpike.Server;
using Xunit;

namespace Nina.SyncSpike.Tests;

[Collection("pg")]
public sealed class SmokeTests(SpikeFixture fx)
{
    [Fact]
    public async Task Server_connects_as_non_superuser_with_rls_and_migration_applied()
    {
        await using var c = await fx.Env.App.OpenConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT current_user, (SELECT rolsuper FROM pg_roles WHERE rolname = current_user), version()", c);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        Assert.Equal("spike_app", r.GetString(0));
        Assert.False(r.GetBoolean(1));
        Assert.StartsWith("PostgreSQL 16", r.GetString(2));
    }

    [Fact]
    public async Task Create_push_and_pull_roundtrip()
    {
        var t = await Trio.CreateAsync(fx.Env);
        var id = t.A.Create("DIAPER_EVENT", Data.Diaper(Data.T0, "DIRTY", "nota privada"));
        var rep = await t.A.SyncAsync();
        Assert.Equal("APPLIED", Assert.Single(rep.Results).Status);
        await t.B.SyncAsync();
        Assert.Equal("DIRTY", Data.S(t.B.Get("DIAPER_EVENT", id), "diaper_type"));
        await t.AssertConvergedAsync(t.A, t.B);
    }
}
