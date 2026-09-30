using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.Filtering;

// Relance périodiquement le téléchargement des listes d'autorisation DNS, selon le même réglage
// "Intervalle de mise à jour des filtres" (FilterUpdateIntervalHours, Paramètres généraux) que
// FilterListUpdateService pour les listes de blocage : AdGuard Home ne distingue pas les deux pour cet
// intervalle. Le premier chargement, lui, est effectué au démarrage par
// IDnsAllowlistService.InitializeAsync (déclenché par DnsProxyService) avant que le service DNS ne
// commence à répondre aux clients ; ce service ne gère que les rafraîchissements suivants.
public sealed class AllowlistUpdateService : BackgroundService
{
    // Intervalle de vérification interne, volontairement grossier : un changement de
    // FilterUpdateIntervalHours met au plus ce délai à être pris en compte, ce qui est négligeable pour
    // un réglage exprimé en heures, et reste très léger en ressources sur un Raspberry Pi.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IDnsAllowlistService allowlistService;
    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<AllowlistUpdateService> logger;

    public AllowlistUpdateService(IDnsAllowlistService allowlistService, ILocalSettingsStore settingsStore, ILogger<AllowlistUpdateService> logger)
    {
        this.allowlistService = allowlistService;
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
                logger.LogError(ex, "Impossible de lire l'intervalle de mise à jour des listes d'autorisation DNS.");
                continue;
            }

            // "Jamais" (0, ou toute valeur négative invalide) : pas de rafraîchissement automatique.
            // Seuls le chargement initial et le bouton "Vérifier les mises à jour" restent actifs.
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
                await allowlistService.RefreshAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Échec du rafraîchissement périodique des listes d'autorisation DNS.");
            }
        }
    }
}
