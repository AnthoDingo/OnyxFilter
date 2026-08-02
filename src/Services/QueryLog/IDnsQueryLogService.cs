using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.QueryLog;

// Journal des requêtes DNS (page /query-log) : une ligne par requête traitée, persistée en base SQLite.
// Respecte "Activer le journal", "Anonymiser l'IP du client", "Rotation des journaux de requêtes" et
// "Domaines ignorés" (Paramètres généraux, /settings/general). Si la base devient inaccessible ou
// corrompue, le journal continue en mémoire (borné) puis, au-delà de 60 secondes d'échec continu, dans un
// fichier quotidien de secours (voir DnsQueryLogService) : la résolution DNS n'est jamais affectée.
public interface IDnsQueryLogService
{
    // Charge les réglages une première fois. À appeler avant le premier RecordQuery (le service DNS s'en
    // charge au démarrage).
    Task InitializeAsync(CancellationToken cancellationToken);

    // Enregistre une requête DNS traitée. N'a aucun effet si "Activer le journal" est désactivé, ou si
    // "domain" figure dans la liste des domaines ignorés. Ne fait aucune E/S : la ligne est simplement mise
    // en file d'attente, écrite en base (ou dans le fichier de secours) par lot un peu plus tard.
    void RecordQuery(
        string domain,
        IPAddress? clientAddress,
        ushort queryType,
        QueryLogReason reason,
        string? reasonDetail,
        string? upstreamServer,
        long processingTimeMs);

    // "take" lignes du journal à partir de "skip", les plus récentes en premier (skip=0 = les plus
    // récentes). Ne lit que la base : les lignes encore en file d'attente ou basculées dans le fichier de
    // secours n'apparaissent pas ici (voir FallbackFilePath).
    // searchText : filtre sur Domain / ClientKey (null = sans filtre).
    // reason     : filtre sur la raison précise (null = toutes les requêtes).
    Task<IReadOnlyList<DnsQueryLogRecord>> GetPageAsync(int skip, int take, string? searchText, QueryLogReason? reason, CancellationToken cancellationToken);

    // "Activer le journal" (Paramètres généraux), pour que la page /query-log puisse afficher un message
    // explicite plutôt qu'un tableau vide lorsque le journal est désactivé.
    bool IsEnabled { get; }

    // False dès qu'une écriture en base a échoué et n'a pas encore réussi depuis (base inaccessible ou
    // corrompue). Le journal continue quand même à collecter (mémoire puis fichier de secours) : ceci ne
    // signale qu'un problème de persistance, jamais une perte de service DNS.
    bool IsDatabaseHealthy { get; }

    // Chemin du fichier de secours du jour, non nul uniquement lorsque le repli fichier est actif (base
    // inaccessible depuis 60 secondes ou plus). Les lignes qui y sont écrites ne sont pas visibles depuis
    // GetPageAsync ni réimportées automatiquement en base au retour de celle-ci.
    string? FallbackFilePath { get; }

    // Efface immédiatement le journal (file d'attente, fichiers de secours, et base). Retourne false si
    // l'effacement en base a échoué (le journal reste alors visible une fois la base rétablie) : l'appelant
    // doit refléter cet échec plutôt que d'annoncer un succès.
    Task<bool> ClearAsync();
}
