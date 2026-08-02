using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.Statistics;

// Statistiques du tableau de bord (Home.razor) : compte les requêtes DNS traitées par heure, en mémoire
// avec persistance périodique en base SQLite (elles survivent à un redémarrage du serveur). Respecte
// "Activer les statistiques", "Intervalle de conservation des statistiques", "Ne pas compter certains
// domaines" et "Anonymiser les adresses IP des clients" (Paramètres généraux).
public interface IDnsStatisticsService
{
    // Charge les réglages une première fois. À appeler avant le premier RecordQuery (le service DNS s'en
    // charge au démarrage).
    Task InitializeAsync(CancellationToken cancellationToken);

    // Enregistre une requête DNS traitée. N'a aucun effet si "Activer les statistiques" est désactivé, ou
    // si "domain" figure dans la liste des domaines ignorés. "upstreamServer"/"upstreamResponseTimeMs"
    // restent nuls quand la réponse ne provient pas d'un serveur en amont (blocage par filtre, ou réponse
    // servie depuis le cache DNS).
    void RecordQuery(
        string domain,
        IPAddress? clientAddress,
        bool blocked,
        string? upstreamServer,
        long? upstreamResponseTimeMs,
        long processingTimeMs);

    // Photo instantanée agrégée sur les dernières 24 heures (ou moins si la rétention configurée est plus
    // courte), pour l'ensemble des panneaux du tableau de bord.
    DnsStatisticsSnapshot GetSnapshot();

    // Nombre de requêtes par client sur la même fenêtre que GetSnapshot(), mais sans la limite des 10
    // entrées les plus fréquentes (DnsStatisticsSnapshot.TopClients) : utilisé par la page "Paramètres du
    // client" (/settings/client), pour la table "Clients d'exécution" et pour afficher le nombre de
    // requêtes de n'importe quel client persistant, pas seulement les 10 plus actifs.
    IReadOnlyList<DnsStatisticsEntry> GetAllClients();

    // Efface immédiatement toutes les statistiques, en mémoire et en base ("Effacer les statistiques",
    // /settings/general).
    void Clear();
}
