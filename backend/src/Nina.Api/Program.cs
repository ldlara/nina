using Nina.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNinaModules();

var app = builder.Build();

app.MapHealthEndpoints();

await app.RunAsync();

// Necessário para WebApplicationFactory nos testes.
public partial class Program;
