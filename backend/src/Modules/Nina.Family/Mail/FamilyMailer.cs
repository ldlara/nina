using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nina.Family.Mail;

/// <summary>
/// Envio do e-mail de convite. Nenhuma implementação real existe neste momento (fica atrás desta interface). O e-mail não leva o nome do
/// bebê (UX 4.7): o convidado só vê convidante e papel antes do aceite.
/// </summary>
public interface IFamilyMailer
{
    Task SendInvitationAsync(string email, string token, string inviterDisplayName, string role, string locale, CancellationToken cancellationToken);
}

public sealed record SentInvitation(string To, string Token, string InviterDisplayName, string Role, string Locale, DateTimeOffset SentAt);

/// <summary>
/// Fake em memória (limite de 1000 mensagens); não envia nada. O token existe aqui só para testes e desenvolvimento;
/// somente em Development ele é escrito no log, para permitir o fluxo sem provedor de e-mail.
/// </summary>
public sealed partial class InMemoryFamilyMailer(TimeProvider time, IHostEnvironment environment, ILogger<InMemoryFamilyMailer> logger) : IFamilyMailer
{
    private const int Capacity = 1000;
    private readonly ConcurrentQueue<SentInvitation> _sent = new();

    public IReadOnlyCollection<SentInvitation> Sent => [.. _sent];

    public Task SendInvitationAsync(string email, string token, string inviterDisplayName, string role, string locale, CancellationToken cancellationToken)
    {
        _sent.Enqueue(new SentInvitation(email, token, inviterDisplayName, role, locale, time.GetUtcNow()));
        if (environment.IsDevelopment())
        {
            LogDevMail(logger, role, token);
        }

        while (_sent.Count > Capacity && _sent.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[DEV FAKE MAIL] Invitation role={Role} token={Token}")]
    private static partial void LogDevMail(ILogger logger, string role, string token);
}
