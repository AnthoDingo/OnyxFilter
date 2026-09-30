using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.DnsForwarding;

public interface IBootstrapResolver
{
    Task InitializeAsync(CancellationToken cancellationToken);

    // Résout un nom d'hôte en adresse IP en utilisant exclusivement les "Serveurs DNS d'amorçage"
    // configurés sur /settings/dns (jamais le résolveur du système d'exploitation). Si "host" est déjà
    // une adresse IP littérale, elle est retournée directement sans requête réseau.
    Task<IPAddress?> ResolveAsync(string host, CancellationToken cancellationToken);
}
