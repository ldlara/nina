using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nina.Identity.Mail;
using Nina.Identity.Tests.Infrastructure;
using Nina.SharedKernel.Http;

namespace Nina.Identity.Tests.Integration;

/// <summary>NR-02 (a API só confia no IP do cliente vindo do BFF autenticado) e NR-12 (forgot sem oráculo de tempo).</summary>
public sealed class ClientIpAndForgotTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    private async Task<int> RegisterManyAsync(int attempts, Func<int, Action<HttpRequestMessage>?> tweak)
    {
        var limited = 0;
        for (var i = 0; i < attempts; i++)
        {
            var r = await Api.PostAsync("/v1/auth/register", new JsonObject
            {
                ["email"] = ApiClient.NewEmail(),
                ["password"] = TestConstants.GoodPassword,
                ["display_name"] = "Teste",
                ["locale"] = "pt-BR",
                ["timezone"] = "America/Sao_Paulo",
                ["consents"] = ApiClient.Consents(),
            }, tweak: tweak(i));
            if (r.Status == HttpStatusCode.TooManyRequests)
            {
                limited++;
            }
        }

        return limited;
    }

    [Fact]
    public async Task Forwarded_for_without_the_internal_secret_is_ignored_so_spoofing_cannot_evade_the_ip_limit()
    {
        // 25 > 20 cadastros/h por IP: cada requisição jura ser outro IP, mas sem segredo todas valem o mesmo cliente.
        var limited = await RegisterManyAsync(25, i => r => r.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}"));

        Assert.True(limited >= 5, $"esperava >= 5 respostas 429, houve {limited}");
    }

    [Fact]
    public async Task A_wrong_internal_secret_is_ignored_like_no_secret()
    {
        var limited = await RegisterManyAsync(25, i => r =>
        {
            r.Headers.Add(InternalHeaders.ClientIp, $"198.51.100.{i + 1}");
            r.Headers.Add(InternalHeaders.Secret, TestConstants.InternalSecret + "x");
        });

        Assert.True(limited >= 5);
    }

    [Fact]
    public async Task With_the_correct_secret_limits_are_per_client_ip()
    {
        // 25 clientes distintos, uma tentativa cada: nenhum é limitado (antes do NR-02 todos dividiam o mesmo balde).
        var limited = await RegisterManyAsync(25, i => ApiClient.FromIp($"198.51.100.{i + 1}"));

        Assert.Equal(0, limited);
    }

    private sealed class SlowResetMailer(InMemoryIdentityMailer inner) : IIdentityMailer
    {
        public Task SendVerificationCodeAsync(string email, string code, string locale, CancellationToken cancellationToken) =>
            inner.SendVerificationCodeAsync(email, code, locale, cancellationToken);

        public Task SendAlreadyRegisteredAsync(string email, string locale, CancellationToken cancellationToken) =>
            inner.SendAlreadyRegisteredAsync(email, locale, cancellationToken);

        public async Task SendPasswordResetAsync(string email, string token, string locale, CancellationToken cancellationToken)
        {
            await Task.Delay(700, cancellationToken); // provedor SMTP lento
            await inner.SendPasswordResetAsync(email, token, locale, cancellationToken);
        }

        public Task SendSecurityNoticeAsync(string email, SecurityNotice notice, string locale, CancellationToken cancellationToken) =>
            inner.SendSecurityNoticeAsync(email, notice, locale, cancellationToken);
    }

    [Fact]
    public async Task Forgot_password_takes_about_the_same_time_for_existing_and_unknown_emails_even_with_a_slow_mailer()
    {
        var registered = await Api.RegisterAndVerifyAsync();
        await using var slow = Factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IBackgroundWork>();
            s.AddSingleton<IBackgroundWork>(sp => sp.GetRequiredService<BackgroundWorkRunner>()); // fila real, fora da requisição
            s.RemoveAll<IIdentityMailer>();
            s.AddSingleton<IIdentityMailer>(sp => new SlowResetMailer(sp.GetRequiredService<InMemoryIdentityMailer>()));
        }));
        var client = new ApiClient(slow.CreateClient(), Factory);

        async Task<double> Time(string email, int n)
        {
            var sw = Stopwatch.StartNew();
            var r = await client.PostAsync("/v1/auth/password/forgot", new JsonObject { ["email"] = email }, tweak: ApiClient.FromIp($"203.0.113.{n}"));
            sw.Stop();
            Assert.Equal(HttpStatusCode.Accepted, r.Status);
            return sw.Elapsed.TotalMilliseconds;
        }

        await Time(ApiClient.NewEmail(), 1); // aquecimento (JIT/pool)
        var existing = new List<double>();
        var unknown = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            existing.Add(await Time(registered.Email, 10 + i));
            unknown.Add(await Time(ApiClient.NewEmail(), 20 + i));
        }

        var e = existing.Order().ToArray()[existing.Count / 2];
        var u = unknown.Order().ToArray()[unknown.Count / 2];
        Assert.True(Math.Max(e, u) / Math.Min(e, u) < 1.5, $"mediana existente={e:F0}ms, inexistente={u:F0}ms");
        Assert.True(e < 600, $"o envio lento de e-mail não pode atrasar a resposta (existente={e:F0}ms)");

        // o e-mail ainda é entregue, em segundo plano
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !Factory.Mailer.Sent.Any(m => m.To == registered.Email && m.Kind == MailKind.PasswordReset))
        {
            await Task.Delay(100);
        }

        Assert.Contains(Factory.Mailer.Sent, m => m.To == registered.Email && m.Kind == MailKind.PasswordReset);
    }
}
