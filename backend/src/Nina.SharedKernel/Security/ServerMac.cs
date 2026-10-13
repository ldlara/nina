using System.Security.Cryptography;
using System.Text;

namespace Nina.SharedKernel.Security;

/// <summary>
/// Assinatura servidor -> banco (NR-03/NR-09). Fatos que só a API pode atestar (reautenticação emitida, código de e-mail gerado e
/// enviado, e-mail verificado pelo provedor social) entram no banco acompanhados de <c>HMAC-SHA256(chave, "proposito|mensagem")</c>;
/// as funções <c>nina.reauth_issue</c>, <c>nina.email_code_issue</c>, <c>nina.email_change_create</c> e
/// <c>nina.register_social_user</c> recusam (<c>NN071</c>) qualquer chamada sem o MAC válido. Quem executa SQL arbitrário como
/// <c>nina_app</c> não tem a chave (configuração da API + tabela <c>nina.server_key</c>, ilegível para os papéis de aplicação).
/// A chave é derivada do <c>Security:MasterKey</c> (<c>HMAC-SHA256(master, "nina.v1:db-mac")</c>) e instalada no banco pelo
/// dono/migrator (<c>nina.provision_server_key</c>).
/// </summary>
public sealed class ServerMac(SecretKeys keys)
{
    /// <summary>Chave de 32 bytes a instalar em <c>nina.server_key</c> (somente o migrator/dono; nunca o papel da aplicação).</summary>
    public byte[] Key => keys.Derive("db-mac");

    /// <summary>MAC de 32 bytes da mensagem canônica <c>proposito|mensagem</c> (UTF-8).</summary>
    public byte[] Sign(string purpose, string message) =>
        HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(purpose + "|" + message));
}
