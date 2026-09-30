namespace OnyxFilter.Services.DnsForwarding;

// Résultat d'une résolution auprès des serveurs en amont : la réponse elle-même (le cas échéant), le
// serveur en amont qui a effectivement répondu (pour les statistiques "Top amonts" et "Temps de réponse
// moyen en amont" du tableau de bord) et le temps écoulé pour l'obtenir. UpstreamServer reste vide quand
// aucun serveur n'a été interrogé (ex. réponse vide IPv6 court-circuitée par "Désactiver la résolution
// IPv6", ou aucun serveur en amont configuré).
public sealed class UpstreamResolutionResult
{
    public static readonly UpstreamResolutionResult Empty = new UpstreamResolutionResult(null, null, 0);

    public UpstreamResolutionResult(byte[]? response, string? upstreamServer, long responseTimeMs)
    {
        Response = response;
        UpstreamServer = upstreamServer;
        ResponseTimeMs = responseTimeMs;
    }

    public byte[]? Response { get; }

    public string? UpstreamServer { get; }

    public long ResponseTimeMs { get; }
}
