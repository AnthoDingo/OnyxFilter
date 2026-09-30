using System;
using System.Linq;

namespace OnyxFilter.Services.DnsForwarding;

// Convertit les lignes saisies dans "Serveurs DNS upstream" (voir /settings/dns) en UpstreamServer.
//
// Pris en charge : adresse IPv4/IPv6 nue (UDP+TCP:53 implicites), udp://, tcp://, tls:// (DNS-over-TLS),
// https:// (DNS-over-HTTPS).
// Non pris en charge pour le moment (ligne ignorée, avertissement journalisé) : h3:// (DoH/HTTP3),
// quic:// (DNS-over-QUIC), sdns:// (DNS Stamps / DNSCrypt), routage conditionnel par domaine
// ([/domaine/]adresse), commentaires (# ...).
public static class UpstreamServerParser
{
    public static UpstreamServer? TryParse(string line, out string? warning)
    {
        warning = null;
        string trimmed = line.Trim();

        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return null;
        }

        if (trimmed.StartsWith('['))
        {
            warning = "Le routage conditionnel par domaine ([/domaine/]adresse) n'est pas encore pris en charge.";
            return null;
        }

        if (trimmed.StartsWith("sdns://", StringComparison.OrdinalIgnoreCase))
        {
            warning = "Les DNS Stamps (sdns://), utilisés pour DNSCrypt notamment, ne sont pas encore pris en charge.";
            return null;
        }

        if (trimmed.StartsWith("quic://", StringComparison.OrdinalIgnoreCase))
        {
            warning = "DNS-over-QUIC (quic://) n'est pas encore pris en charge.";
            return null;
        }

        if (trimmed.StartsWith("h3://", StringComparison.OrdinalIgnoreCase))
        {
            warning = "DNS-over-HTTPS forcé en HTTP/3 (h3://) n'est pas encore pris en charge.";
            return null;
        }

        // Une ligne peut contenir plusieurs adresses séparées par des espaces (ex. amonts multiples
        // pour un même domaine conditionnel) : seule la première est retenue pour le moment.
        string candidate = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        UpstreamServer? server = TryParseScheme(candidate, "udp://", UpstreamTransport.Udp, 53)
            ?? TryParseScheme(candidate, "tcp://", UpstreamTransport.Tcp, 53)
            ?? TryParseScheme(candidate, "tls://", UpstreamTransport.Tls, 853)
            ?? TryParseScheme(candidate, "https://", UpstreamTransport.Https, 443);

        if (server is not null)
        {
            return server;
        }

        return ParsePlainAddress(candidate, trimmed);
    }

    private static UpstreamServer? TryParseScheme(string candidate, string scheme, UpstreamTransport transport, int defaultPort)
    {
        if (!candidate.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string remainder = candidate.Substring(scheme.Length);
        string hostPort = remainder;
        string path = string.Empty;

        int slashIndex = remainder.IndexOf('/');

        if (slashIndex >= 0)
        {
            hostPort = remainder.Substring(0, slashIndex);
            path = remainder.Substring(slashIndex);
        }

        (string host, int port) = SplitHostPort(hostPort, defaultPort);

        if (host.Length == 0)
        {
            return null;
        }

        return new UpstreamServer
        {
            Transport = transport,
            Host = host,
            Port = port,
            Path = path,
            OriginalText = candidate,
        };
    }

    private static UpstreamServer? ParsePlainAddress(string candidate, string originalLine)
    {
        (string host, int port) = SplitHostPort(candidate, 53);

        if (host.Length == 0)
        {
            return null;
        }

        return new UpstreamServer
        {
            Transport = UpstreamTransport.Udp,
            Host = host,
            Port = port,
            Path = string.Empty,
            OriginalText = originalLine,
        };
    }

    private static (string Host, int Port) SplitHostPort(string hostPort, int defaultPort)
    {
        if (hostPort.StartsWith('['))
        {
            // Adresse IPv6 entre crochets, avec port optionnel : [::1]:53
            int closingBracket = hostPort.IndexOf(']');

            if (closingBracket > 0)
            {
                string bracketedHost = hostPort.Substring(1, closingBracket - 1);
                string rest = hostPort.Substring(closingBracket + 1);

                if (rest.StartsWith(':') && int.TryParse(rest.Substring(1), out int bracketedPort))
                {
                    return (bracketedHost, bracketedPort);
                }

                return (bracketedHost, defaultPort);
            }
        }

        int colonCount = hostPort.Count(character => character == ':');

        if (colonCount == 1)
        {
            int colonIndex = hostPort.IndexOf(':');
            string host = hostPort.Substring(0, colonIndex);
            string portText = hostPort.Substring(colonIndex + 1);

            if (int.TryParse(portText, out int port))
            {
                return (host, port);
            }
        }

        // Pas de port explicite, ou adresse IPv6 nue sans crochets (plusieurs ':').
        return (hostPort, defaultPort);
    }
}
