namespace OnyxFilter.Services.DnsForwarding;

public enum UpstreamTransport
{
    Udp,
    Tcp,
    Tls,
    Https
}

// Un serveur DNS en amont, tel qu'issu du parsing d'une ligne de la liste "Serveurs DNS upstream".
public sealed class UpstreamServer
{
    public UpstreamTransport Transport { get; init; }

    public string Host { get; init; } = string.Empty;

    public int Port { get; init; }

    // Chemin HTTP pour le DNS-over-HTTPS (ex. "/dns-query"). Ignoré pour les autres transports.
    public string Path { get; init; } = string.Empty;

    public string OriginalText { get; init; } = string.Empty;

    public override string ToString()
    {
        return OriginalText;
    }
}
