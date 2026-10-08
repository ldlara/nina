using Nina.SyncSpike;
using Nina.SyncSpike.Harness;

// Uso: dotnet run -c Release --project backend/spikes/Nina.SyncSpike -- bench [arquivo-de-saída]
// Sobe um PostgreSQL 16 temporário, aplica 0001_init.sql + extras, roda as medições e remove tudo.
var cmd = args.FirstOrDefault() ?? "bench";
if (cmd != "bench")
{
    Console.Error.WriteLine("comando desconhecido: " + cmd + " (use: bench)");
    return 2;
}
await using var env = await SpikeEnv.StartAsync();
var report = await Bench.RunAsync(env);
if (args.Length > 1) await File.WriteAllTextAsync(args[1], report);
return 0;
