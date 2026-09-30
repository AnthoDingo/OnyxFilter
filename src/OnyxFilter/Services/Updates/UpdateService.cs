using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.Updates;

public sealed class UpdateService : IUpdateService
{
    // Résultat réutilisé par les vérifications non forcées (ouverture de page, tâche périodique).
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    // Délai minimal entre deux vérifications forcées réussies (bouton « Rechercher ») : ménage le quota de
    // l'API GitHub (60 appels par heure sans authentification).
    private static readonly TimeSpan MinimumForcedInterval = TimeSpan.FromSeconds(20);

    // Laisse le temps à l'interface et à l'API d'afficher l'état « Redémarrage » avant l'arrêt.
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    private readonly GitHubReleaseClient releaseClient;
    private readonly UpdateInstaller installer;
    private readonly ILocalSettingsStore settingsStore;
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly UpdateOptions options;
    private readonly ILogger<UpdateService> logger;

    // Une seule vérification ou installation à la fois.
    private readonly SemaphoreSlim operationLock = new SemaphoreSlim(1, 1);
    private readonly object statusLock = new object();
    private UpdateStatus status;

    // Réglage « préversions » de la dernière vérification : le résultat en cache ne vaut que pour lui.
    private bool? lastCheckIncludedPreReleases;

    public UpdateService(
        GitHubReleaseClient releaseClient,
        UpdateInstaller installer,
        ILocalSettingsStore settingsStore,
        IHostApplicationLifetime applicationLifetime,
        IOptions<UpdateOptions> options,
        ILogger<UpdateService> logger)
    {
        this.releaseClient = releaseClient;
        this.installer = installer;
        this.settingsStore = settingsStore;
        this.applicationLifetime = applicationLifetime;
        this.options = options.Value;
        this.logger = logger;

        status = new UpdateStatus
        {
            CurrentVersion = AppVersion.Display,
            IsDevelopmentBuild = AppVersion.Current.IsDevelopmentBuild,
            State = UpdateState.Idle,
            InstallBlocker = UpdateEnvironment.GetInstallBlocker(this.options),
            LastUpdate = UpdateInstaller.ReadLastUpdate(AppVersion.Current),
        };
    }

    public event EventHandler? StatusChanged;

    public UpdateStatus Status
    {
        get
        {
            lock (statusLock)
            {
                return status;
            }
        }
    }

    public async Task<UpdateStatus> CheckAsync(bool force, CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken);

        try
        {
            UpdateStatus current = Status;

            if (current.State == UpdateState.Restarting)
            {
                return current;
            }

            UpdateSettingsData settings = (await settingsStore.LoadAsync()).Updates;

            if (current.LastCheckedUtc is DateTimeOffset lastChecked
                && current.LastError is null
                && lastCheckIncludedPreReleases == settings.IncludePreReleases)
            {
                TimeSpan age = DateTimeOffset.UtcNow - lastChecked;

                if ((!force && age < CacheDuration) || (force && age < MinimumForcedInterval))
                {
                    return current;
                }
            }

            SetStatus(s => s with { State = UpdateState.Checking, LastError = null, Progress = null });

            try
            {
                ReleaseInfo? latest = await releaseClient.GetLatestAsync(settings.IncludePreReleases, cancellationToken);
                lastCheckIncludedPreReleases = settings.IncludePreReleases;
                bool available = latest is not null && latest.Version > AppVersion.Current;

                SetStatus(s => s with
                {
                    State = available ? UpdateState.UpdateAvailable : UpdateState.UpToDate,
                    LatestRelease = latest,
                    IsUpdateAvailable = available,
                    LastCheckedUtc = DateTimeOffset.UtcNow,
                    InstallBlocker = GetInstallBlocker(latest),
                });

                if (available)
                {
                    logger.LogInformation("Nouvelle version d'OnyxFilter disponible : {Latest} (version en cours : {Current}).", latest!.Version, AppVersion.Current);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Vérification des mises à jour impossible.");

                // Une publication déjà trouvée reste signalée malgré l'échec de cette vérification.
                SetStatus(s => s with
                {
                    State = s.IsUpdateAvailable ? UpdateState.UpdateAvailable : UpdateState.Failed,
                    LastError = "Vérification impossible : " + Describe(ex),
                    LastCheckedUtc = DateTimeOffset.UtcNow,
                });
            }

            return Status;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<UpdateStatus> InstallAsync(CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken);

        try
        {
            UpdateStatus current = Status;

            if (current.State == UpdateState.Restarting)
            {
                return current;
            }

            if (!current.IsUpdateAvailable || current.LatestRelease is null)
            {
                SetStatus(s => s with { LastError = "Aucune mise à jour à installer : lancez d'abord une vérification." });
                return Status;
            }

            ReleaseInfo release = current.LatestRelease;
            string? blocker = GetInstallBlocker(release);

            if (blocker is not null)
            {
                SetStatus(s => s with { InstallBlocker = blocker, LastError = blocker });
                return Status;
            }

            logger.LogInformation("Installation de la mise à jour {Latest} (version en cours : {Current}).", release.Version, AppVersion.Current);

            try
            {
                // Pas d'annulation par l'appelant : une installation lancée va à son terme même si la page
                // ou la requête API qui l'a déclenchée se ferme.
                await installer.InstallAsync(
                    release,
                    AppVersion.Current,
                    (state, progress) => SetStatus(s => s with { State = state, Progress = progress, LastError = null }),
                    CancellationToken.None);

                SetStatus(s => s with { State = UpdateState.Restarting, Progress = null });
                ScheduleRestart(release.Version);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Échec de la mise à jour vers {Latest}.", release.Version);
                SetStatus(s => s with
                {
                    State = UpdateState.Failed,
                    Progress = null,
                    LastError = "Mise à jour non installée : " + Describe(ex),
                });
            }

            return Status;
        }
        finally
        {
            operationLock.Release();
        }
    }

    // Arrêt propre de l'application ; systemd (Restart=always, voir deploy/onyxfilter.service) la relance
    // aussitôt sur les fichiers de la nouvelle version.
    private void ScheduleRestart(SemanticVersion newVersion)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(RestartDelay);
            logger.LogInformation("Redémarrage d'OnyxFilter pour passer à la version {Version}.", newVersion);
            applicationLifetime.StopApplication();
        });
    }

    private string? GetInstallBlocker(ReleaseInfo? release)
    {
        string? environmentBlocker = UpdateEnvironment.GetInstallBlocker(options);

        if (environmentBlocker is not null || release is null)
        {
            return environmentBlocker;
        }

        if (release.Package is null)
        {
            return $"La publication {release.TagName} ne contient pas d'archive pour {UpdateEnvironment.SupportedRuntimeIdentifier} : installez-la à la main depuis GitHub.";
        }

        if (release.Checksums is null)
        {
            return $"La publication {release.TagName} ne fournit pas de fichier checksums.txt : l'archive ne peut pas être vérifiée.";
        }

        return null;
    }

    private void SetStatus(Func<UpdateStatus, UpdateStatus> change)
    {
        lock (statusLock)
        {
            status = change(status);
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Describe(Exception exception) => exception switch
    {
        UpdateException => exception.Message,
        OperationCanceledException => "délai d'attente dépassé.",
        HttpRequestException => "erreur réseau (" + exception.Message + ").",
        JsonException => "réponse illisible du serveur de publications.",
        _ => exception.Message,
    };
}
