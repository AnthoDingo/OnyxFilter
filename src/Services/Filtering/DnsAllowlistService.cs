using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.Filtering;

// Implémente IDnsAllowlistService : mêmes principes que DnsFilterService (chargement du cache disque au
// démarrage, rafraîchissement réseau en arrière-plan), mais pour la page "Listes d'autorisation DNS"
// (/filters/allowlists). Le téléchargement/cache/analyse des listes elles-mêmes est délégué à
// DomainListRepository, partagé avec DnsFilterService.
public sealed class DnsAllowlistService : IDnsAllowlistService, IDisposable
{
    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<DnsAllowlistService> logger;
    private readonly DomainListRepository repository;
    private readonly object syncRoot = new object();

    // Sérialise les rafraîchissements (bouton manuel, minuterie périodique, changement de réglages) pour
    // ne jamais télécharger les mêmes listes en parallèle.
    private readonly SemaphoreSlim refreshLock = new SemaphoreSlim(1, 1);

    private IReadOnlyList<FilterListEntry> configuredLists = Array.Empty<FilterListEntry>();

    private CompactDomainSet allowedDomains = CompactDomainSet.Empty;
    private IReadOnlyList<FilterListStatus> listStatuses = Array.Empty<FilterListStatus>();
    private DateTime? lastRefreshUtc;

    public DnsAllowlistService(ILocalSettingsStore settingsStore, IHostEnvironment hostEnvironment, ILogger<DnsAllowlistService> logger)
    {
        this.settingsStore = settingsStore;
        this.logger = logger;

        string cacheDirectory = Path.Combine(hostEnvironment.ContentRootPath, "allowlists-cache");
        repository = new DomainListRepository(cacheDirectory, "liste d'autorisation", logger);

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

        // Chargement depuis le cache disque uniquement (aucun appel réseau) : l'autorisation est active
        // dès le démarrage du service DNS, même si les serveurs des listes d'autorisation sont
        // injoignables.
        (CompactDomainSet initialDomains, List<FilterListStatus> initialStatuses, _) = await repository.LoadFromDiskCacheAsync(lists, cancellationToken);

        lock (syncRoot)
        {
            allowedDomains = initialDomains;
            listStatuses = initialStatuses;
        }

        logger.LogInformation(
            "Listes d'autorisation DNS chargées depuis le cache disque au démarrage : {DomainCount} domaine(s).",
            initialDomains.Count);

        // Le rafraîchissement réseau se fait en arrière-plan : il ne doit jamais retarder le démarrage du
        // service DNS si un serveur de liste d'autorisation est lent ou injoignable.
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
            logger.LogError(ex, "Échec du premier rafraîchissement en arrière-plan des listes d'autorisation DNS.");
        }
    }

    public bool IsAllowed(string domain)
    {
        CompactDomainSet domains;

        lock (syncRoot)
        {
            domains = allowedDomains;
        }

        return domains.Count != 0 && DomainListRepository.ContainsDomainOrParent(domain, domains);
    }

    public FilterListsSnapshot GetSnapshot()
    {
        lock (syncRoot)
        {
            return new FilterListsSnapshot
            {
                Lists = listStatuses,
                TotalDomainCount = allowedDomains.Count,
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
            (CompactDomainSet merged, List<FilterListStatus> statuses, _) = await repository.RefreshAsync(lists, cancellationToken);

            lock (syncRoot)
            {
                allowedDomains = merged;
                listStatuses = statuses;
                lastRefreshUtc = DateTime.UtcNow;
            }

            logger.LogInformation(
                "Listes d'autorisation DNS mises à jour : {DomainCount} domaine(s) au total, {ListCount} liste(s) configurée(s).",
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
    // modifiée/activation).
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
            logger.LogError(ex, "Impossible de recharger les réglages des listes d'autorisation DNS.");
        }
    }

    // Doit être appelé sous le verrou "syncRoot".
    private void ApplySettingsSnapshot(AppLocalSettings settings)
    {
        configuredLists = settings.Allowlist.Lists;
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
