using System.Collections.Concurrent;

namespace Nina.Identity.Mail;

public enum SecurityNotice
{
    PasswordChanged,
    PasswordReset,
    IdentityLinked,
}

public enum MailKind
{
    VerificationCode,
    AlreadyRegistered,
    PasswordReset,
    SecurityNotice,
}

/// <summary>Envio de e-mails transacionais do Identity. Nenhuma implementação real existe neste momento (fica atrás desta interface).</summary>
public interface IIdentityMailer
{
    Task SendVerificationCodeAsync(string email, string code, string locale, CancellationToken cancellationToken);

    /// <summary>Aviso neutro a quem tentou cadastrar um e-mail que já tem conta (a resposta HTTP continua uniforme).</summary>
    Task SendAlreadyRegisteredAsync(string email, string locale, CancellationToken cancellationToken);

    Task SendPasswordResetAsync(string email, string token, string locale, CancellationToken cancellationToken);

    Task SendSecurityNoticeAsync(string email, SecurityNotice notice, string locale, CancellationToken cancellationToken);
}

public sealed record SentMail(string To, MailKind Kind, string? Secret, SecurityNotice? Notice, DateTimeOffset SentAt);

/// <summary>
/// Implementação fake: guarda as mensagens em memória (limite de 1000) e não envia nada. É o padrão até existir provedor real;
/// o campo <c>Secret</c> existe só para testes e ambiente de desenvolvimento.
/// </summary>
public sealed class InMemoryIdentityMailer(TimeProvider time) : IIdentityMailer
{
    private const int Capacity = 1000;
    private readonly ConcurrentQueue<SentMail> _sent = new();

    public IReadOnlyCollection<SentMail> Sent => [.. _sent];

    public Task SendVerificationCodeAsync(string email, string code, string locale, CancellationToken cancellationToken) =>
        Add(new SentMail(email, MailKind.VerificationCode, code, null, time.GetUtcNow()));

    public Task SendAlreadyRegisteredAsync(string email, string locale, CancellationToken cancellationToken) =>
        Add(new SentMail(email, MailKind.AlreadyRegistered, null, null, time.GetUtcNow()));

    public Task SendPasswordResetAsync(string email, string token, string locale, CancellationToken cancellationToken) =>
        Add(new SentMail(email, MailKind.PasswordReset, token, null, time.GetUtcNow()));

    public Task SendSecurityNoticeAsync(string email, SecurityNotice notice, string locale, CancellationToken cancellationToken) =>
        Add(new SentMail(email, MailKind.SecurityNotice, null, notice, time.GetUtcNow()));

    private Task Add(SentMail mail)
    {
        _sent.Enqueue(mail);
        while (_sent.Count > Capacity && _sent.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }
}
