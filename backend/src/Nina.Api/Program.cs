using Nina.Api;
using Nina.SharedKernel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNinaSharedKernel(builder.Configuration);
builder.Services.AddNinaModules(builder.Configuration);

var app = builder.Build();

app.UseNinaPipeline();

app.MapHealthEndpoints();
app.MapNinaModules();

await app.RunAsync();

// Necessário para WebApplicationFactory nos testes.
public partial class Program;
