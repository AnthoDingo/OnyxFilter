using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OnyxFilter.Services.Updates;

// Recherche périodique d'une nouvelle version (« Rechercher automatiquement », page « Mises à jour ») :
// une première fois peu après le démarrage, puis toutes les 12 heures. N'installe jamais rien.
public sealed class UpdateCheckBackgroundService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly IUpdateService updateService;
    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<UpdateCheckBackgroundService> logger;

    public UpdateCheckBackgroundService(IUpdateService updateService, ILocalSettingsStore settingsStore, ILogger<UpdateCheckBackgroundService> logger)
    {
        this.updateService = updateService;
        this.settingsStore = settingsStore;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if ((await settingsStore.LoadAsync()).Updates.AutoCheck)
                    {
                        await updateService.CheckAsync(force: false, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Recherche périodique des mises à jour impossible.");
                }

                await Task.Delay(CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Arrêt de l'application.
        }
    }
}
