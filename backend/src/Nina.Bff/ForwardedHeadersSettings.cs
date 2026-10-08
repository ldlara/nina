using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Nina.Bff;

/// <summary>
/// Configuração <c>ForwardedHeaders:*</c> (NR-02). O BFF só aceita X-Forwarded-For/-Proto de proxies da lista; de qualquer
/// outro par o cabeçalho é ignorado e o IP do cliente é o do socket. Sem nenhuma entrada vale só o loopback (fail-safe).
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Redes CIDR do ingress/roteador (ex.: <c>10.128.0.0/14</c>).</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>IPs individuais de proxies confiáveis.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Quantas entradas do X-Forwarded-For (da direita para a esquerda) são consumidas; 1 = só o que o roteador anexou.</summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>Exige o mesmo número de entradas em X-Forwarded-For e X-Forwarded-Proto.</summary>
    public bool RequireHeaderSymmetry { get; set; }

    public void Apply(ForwardedHeadersOptions options)
    {
        if (ForwardLimit < 1)
        {
            throw new InvalidOperationException("ForwardedHeaders:ForwardLimit deve ser >= 1.");
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = ForwardLimit;
        options.RequireHeaderSymmetry = RequireHeaderSymmetry;
        if (KnownNetworks.Length == 0 && KnownProxies.Length == 0)
        {
            return; // mantém o padrão do ASP.NET: só loopback
        }

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var network in KnownNetworks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }

        foreach (var proxy in KnownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }
    }
}
