using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.DnsForwarding;

namespace OnyxFilter.Services.Filtering;

// Applique "Bloquer les domaines à l'aide de filtres" (Paramètres généraux) : télécharge les listes de
// blocage abonnées (/filters/blocklists), et construit la réponse à renvoyer pour un domaine bloqué
// selon le "Mode de blocage" configuré sur /settings/dns. Le téléchargement/cache/analyse des listes
// elles-mêmes est délégué à DomainListRepository, partagé avec DnsAllowlistService (/filters/allowlists).
//
// Utilise la résolution DNS habituelle du système d'exploitation pour télécharger les listes elles-mêmes
// (contrairement aux serveurs DNS en amont, qui passent par les serveurs d'amorçage pour éviter toute
// dépendance circulaire) : il ne s'agit ici que d'une tâche de maintenance périodique, pas du chemin de
// résolution des requêtes des clients.
public sealed class DnsFilterService : IDnsFilterService, IDisposable
{
    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<DnsFilterService> logger;
    private readonly DomainListRepository repository;
    private readonly object syncRoot = new object();

    // Sérialise les rafraîchissements (bouton manuel, minuterie périodique, changement de réglages) pour
    // ne jamais télécharger les mêmes listes en parallèle.
    private readonly SemaphoreSlim refreshLock = new SemaphoreSlim(1, 1);

    private bool blockingEnabled;
    private DnsBlockingMode blockingMode = DnsBlockingMode.Default;
    private string customBlockingIpv4 = string.Empty;
    private string customBlockingIpv6 = string.Empty;
    private IReadOnlyList<FilterListEntry> configuredLists = Array.Empty<FilterListEntry>();

    private CompactDomainSet blockedDomains = CompactDomainSet.Empty;

    // Ensembles de domaines par liste (nom + CompactDomainSet) pour identifier laquelle a causé le
    // blocage dans TryBuildBlockResponse. Construits en parallèle du merged set lors de chaque
    // rafraîchissement ; la surcharge mémoire reste faible car les listes se chevauchent peu en pratique.
    private IReadOnlyList<(string Name, CompactDomainSet Domains)> blockedDomainsPerList = Array.Empty<(string, CompactDomainSet)>();
    private IReadOnlyList<FilterListStatus> listStatuses = Array.Empty<FilterListStatus>();
    private DateTime? lastRefreshUtc;

    public DnsFilterService(ILocalSettingsStore settingsStore, IHostEnvironment hostEnvironment, ILogger<DnsFilterService> logger)
    {
        this.settingsStore = settingsStore;
        this.logger = logger;

        string cacheDirectory = Path.Combine(hostEnvironment.ContentRootPath, "filterlists-cache");
        repository = new DomainListRepository(cacheDirectory, "liste de blocage", logger);

        this.settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        AppLocalSettings settings = await settingsStore.LoadAsync();
        IReadOnlyList<FilterListEntry> lists;

        lock (syncRoot)
        {
            ApplySettingsSnapshot(settings);
            lists = configuredLists;
        }

        // Chargement depuis le cache disque uniquement (aucun appel réseau) : le blocage est actif dès le
        // démarrage du service DNS, même si les serveurs des listes de blocage sont injoignables.
        (CompactDomainSet initialDomains, List<FilterListStatus> initialStatuses, IReadOnlyList<(string Name, CompactDomainSet Domains)> initialPerList) = await repository.LoadFromDiskCacheAsync(lists, cancellationToken);

        lock (syncRoot)
        {
            blockedDomains = initialDomains;
            blockedDomainsPerList = initialPerList;
            listStatuses = initialStatuses;
        }

        logger.LogInformation(
            "Listes de blocage DNS chargées depuis le cache disque au démarrage : {DomainCount} domaine(s).",
            initialDomains.Count);

        // Le rafraîchissement réseau se fait en arrière-plan : il ne doit jamais retarder le démarrage du
        // service DNS si un serveur de liste de blocage est lent ou injoignable.
        _ = RefreshInBackgroundAsync(cancellationToken);
    }

    private async Task RefreshInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Échec du premier rafraîchissement en arrière-plan des listes de blocage DNS.");
        }
    }

    public bool TryBuildBlockResponse(byte[] query, out byte[]? response, out string? matchedListName)
    {
        response = null;
        matchedListName = null;

        bool enabled;
        DnsBlockingMode mode;
        string ipv4;
        string ipv6;
        CompactDomainSet domains;
        IReadOnlyList<(string Name, CompactDomainSet Domains)> perList;

        lock (syncRoot)
        {
            enabled = blockingEnabled;
            mode = blockingMode;
            ipv4 = customBlockingIpv4;
            ipv6 = customBlockingIpv6;
            domains = blockedDomains;
            perList = blockedDomainsPerList;
        }

        if (!enabled || domains.Count == 0)
        {
            return false;
        }

        if (!DnsMessageParser.TryReadQuestionName(query, out string name) || !DomainListRepository.ContainsDomainOrParent(name, domains))
        {
            return false;
        }

        if (!DnsMessageParser.TryReadQuestionType(query, out ushort queryType))
        {
            return false;
        }

        // Identification de la liste ayant causé le blocage (première correspondance trouvée).
        foreach ((string listName, CompactDomainSet listDomains) in perList)
        {
            if (DomainListRepository.ContainsDomainOrParent(name, listDomains))
            {
                matchedListName = listName;
                break;
            }
        }

        response = DnsBlockResponseBuilder.Build(query, queryType, mode, ipv4, ipv6);
        return true;
    }

    public FilterListsSnapshot GetSnapshot()
    {
        lock (syncRoot)
        {
            return new FilterListsSnapshot
            {
                Lists = listStatuses,
                TotalDomainCount = blockedDomains.Count,
                LastRefreshUtc = lastRefreshUtc,
            };
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FilterListEntry> lists;

        lock (syncRoot)
        {
            lists = configuredLists;
        }

        await refreshLock.WaitAsync(cancellationToken);

        try
        {
            (CompactDomainSet merged, List<FilterListStatus> statuses, IReadOnlyList<(string Name, CompactDomainSet Domains)> perList) = await repository.RefreshAsync(lists, cancellationToken);

            lock (syncRoot)
            {
                blockedDomains = merged;
                blockedDomainsPerList = perList;
                listStatuses = statuses;
                lastRefreshUtc = DateTime.UtcNow;
            }

            logger.LogInformation(
                "Listes de blocage DNS mises à jour : {DomainCount} domaine(s) au total, {ListCount} liste(s) configurée(s).",
                merged.Count,
                statuses.Count);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private void OnSettingsChanged()
    {
        _ = ReloadSettingsAndMaybeRefreshAsync();
    }

    // Recharge les réglages à chaque enregistrement (toutes pages confondues), mais ne relance un
    // téléchargement réseau que si la liste d'abonnements elle-même a changé (ajout/suppression/URL
    // modifiée/activation) : les autres réglages (mode de blocage, activation du filtrage) n'ont pas
    // besoin de retélécharger quoi que ce soit.
    private async Task ReloadSettingsAndMaybeRefreshAsync()
    {
        try
        {
            AppLocalSettings settings = await settingsStore.LoadAsync();
            bool listsChanged;

            lock (syncRoot)
            {
                IReadOnlyList<FilterListEntry> previousLists = configuredLists;
                ApplySettingsSnapshot(settings);
                listsChanged = !AreListsEquivalent(previousLists, configuredLists);
            }

            if (listsChanged)
            {
                await RefreshAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de recharger les réglages des listes de blocage DNS.");
        }
    }

    // Doit être appelé sous le verrou "syncRoot".
    private void ApplySettingsSnapshot(AppLocalSettings settings)
    {
        blockingEnabled = settings.General.BlockDomainsWithFilters;
        blockingMode = settings.Dns.BlockingMode;
        customBlockingIpv4 = settings.Dns.CustomBlockingIpv4;
        customBlockingIpv6 = settings.Dns.CustomBlockingIpv6;
        configuredLists = settings.FilterLists.Lists;
    }

    private static bool AreListsEquivalent(IReadOnlyList<FilterListEntry> first, IReadOnlyList<FilterListEntry> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (int index = 0; index < first.Count; index++)
        {
            FilterListEntry a = first[index];
            FilterListEntry b = second[index];

            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                || !string.Equals(a.Url, b.Url, StringComparison.Ordinal)
                || a.Enabled != b.Enabled)
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
        repository.Dispose();
        refreshLock.Dispose();
    }
}
