namespace OnyxFilter.Models.Settings;

// Une règle de la page "Réécritures DNS" (/filters/rewrites) : "Domain" est soit un domaine exact
// ("example.com"), soit un motif "*.example.com" couvrant ses sous-domaines (jamais le domaine de base
// lui-même). "Answer" est soit une adresse IPv4/IPv6 (réponse directe), soit un autre nom de domaine
// (réponse par chaîne CNAME, résolue auprès des serveurs en amont configurés).
public sealed class DnsRewriteEntry
{
    public string Id { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public string Answer { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
