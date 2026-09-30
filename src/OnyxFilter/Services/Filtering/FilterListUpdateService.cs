using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;

namespace OnyxFilter.Services.Filtering;

// Relance périodiquement le téléchargement des listes de blocage DNS, selon "Intervalle de mise à jour
// des filtres" (FilterUpdateIntervalHours, Paramètres généraux). Le premier chargement, lui, est effectué
// au démarrage par IDnsFilterService.InitializeAsync (déclenché par DnsProxyService) avant que le service
// DNS ne commence à répondre aux clients ; ce service ne gère que les rafraîchissements suivants.
public sealed class FilterListUpdateService : BackgroundService
{
    // Intervalle de vérification interne, volontairement grossier : un changement de
    // FilterUpdateIntervalHours met au plus ce délai à être pris en compte, ce qui est négligeable pour
    // un réglage exprimé en heures, et reste très léger en ressources sur un Raspberry Pi.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IDnsFilterService filterService;
    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<FilterListUpdateService> logger;

    public FilterListUpdateService(IDnsFilterService filterService, ILocalSettingsStore settingsStore, ILogger<FilterListUpdateService> logger)
    {
        this.filterService = filterService;
        this.settingsStore = settingsStore;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime lastRefreshTriggeredUtc = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            int intervalHours;

            try
            {
                AppLocalSettings settings = await settingsStore.LoadAsync();
                intervalHours = settings.General.FilterUpdateIntervalHours;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Impossible de lire l'intervalle de mise à jour des listes de blocage DNS.");
                continue;
            }

            // "Jamais" (0, ou toute valeur négative invalide) : pas de rafraîchissement automatique.
            // Seuls le chargement initial et le bouton "Mettre à jour maintenant" restent actifs.
            if (intervalHours <= 0)
            {
                continue;
            }

            if (DateTime.UtcNow - lastRefreshTriggeredUtc < TimeSpan.FromHours(intervalHours))
            {
                continue;
            }

            lastRefreshTriggeredUtc = DateTime.UtcNow;

            try
            {
                await filterService.RefreshAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Échec du rafraîchissement périodique des listes de blocage DNS.");
            }
        }
    }
}
