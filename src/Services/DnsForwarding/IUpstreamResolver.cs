using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.DnsForwarding;

public interface IUpstreamResolver
{
    // Charge la liste des serveurs en amont depuis les paramètres. À appeler une fois avant le premier
    // ResolveAsync (le service DNS s'en charge au démarrage).
    Task InitializeAsync(CancellationToken cancellationToken);

    // Vrai si "Activer le sous-réseau client (EDNS)" est actif. Comme la réponse dépend alors du
    // sous-réseau du client demandeur, l'appelant (DnsProxyService) doit ignorer le cache DNS pour ces
    // requêtes : la clé de cache (nom+type+classe) ne distingue pas les clients.
    bool IsClientSubnetEnabled { get; }

    // Transmet une requête DNS (format "wire", tel que reçu du client) à un ou plusieurs serveurs en
    // amont, et retourne la première réponse valide obtenue (UpstreamResolutionResult.Response reste nul
    // si aucun serveur n'a répondu). "clientAddress" (adresse du client d'origine, si connue) sert
    // uniquement à construire l'option EDNS Client Subnet quand elle est activée ; elle n'est jamais
    // transmise telle quelle. Le résultat inclut aussi le serveur en amont ayant répondu et le temps de
    // réponse, utilisés par le moteur de statistiques du tableau de bord.
    Task<UpstreamResolutionResult> ResolveAsync(byte[] query, IPAddress? clientAddress, CancellationToken cancellationToken);
}
