using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.DnsForwarding;

// Pipeline de résolution DNS partagé entre les points d'écoute (port 53 UDP/TCP via DnsProxyService,
// DNS-over-TLS via DnsOverTlsService) : filtres de blocage, cache DNS, serveurs en amont et
// statistiques du tableau de bord. Les vérifications propres à la connexion (client autorisé, limite
// de requêtes) restent à la charge de chaque point d'écoute.
public interface IDnsQueryPipeline
{
    // Initialise toutes les dépendances du service DNS (résolveur amont, cache, limite de débit,
    // contrôle d'accès, filtres, statistiques). Idempotent : chaque point d'écoute l'appelle au
    // démarrage, seule la première invocation effectue le travail.
    Task InitializeAsync(CancellationToken cancellationToken);

    // Vrai lorsque le domaine demandé figure dans la liste des domaines interdits : la requête ne doit
    // être traitée d'aucune façon (ni réponse, ni journal, ni statistiques).
    bool IsQueryDisallowed(byte[] query);

    // Résout une requête DNS (format "wire") : réponse de blocage si le domaine est filtré, sinon cache
    // DNS puis serveurs en amont. Alimente les statistiques du tableau de bord pour chaque requête.
    Task<byte[]?> ResolveAsync(byte[] query, IPAddress? clientAddress, CancellationToken cancellationToken);
}
