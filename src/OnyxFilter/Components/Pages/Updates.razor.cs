using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using OnyxFilter.Services.Updates;

namespace OnyxFilter.Components.Pages;

public partial class Updates : ComponentBase, IDisposable
{
    private static CultureInfo DisplayCulture => CultureInfo.CurrentCulture;

    [Inject]
    public IUpdateService UpdateService { get; set; } = default!;

    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

    [Inject]
    public IOptions<UpdateOptions> UpdateOptions { get; set; } = default!;

    private UpdateStatus Status { get; set; } = default!;

    private bool AutoCheck { get; set; } = true;

    private bool IncludePreReleases { get; set; }

    private bool ShowInstallConfirm { get; set; }

    private string? SettingsMessage { get; set; }

    private string ReleasesUrl => "https://github.com/" + UpdateOptions.Value.Repository + "/releases";

    private string BackupDirectory => UpdateInstaller.BackupDirectory;

    private string InstallDirectory => UpdateEnvironment.InstallDirectory.TrimEnd('/');

    private int ProgressPercent => (int)Math.Round((Status.Progress ?? 0) * 100);

    protected override async Task OnInitializedAsync()
    {
        Status = UpdateService.Status;
        UpdateService.StatusChanged += OnStatusChanged;

        UpdateSettingsData settings = (await SettingsStore.LoadAsync()).Updates;
        AutoCheck = settings.AutoCheck;
        IncludePreReleases = settings.IncludePreReleases;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        // Première visite depuis le démarrage : vérification sans attendre la tâche périodique (le
        // résultat récent éventuel est réutilisé, voir IUpdateService.CheckAsync).
        if (firstRender && Status.State == UpdateState.Idle)
        {
            _ = UpdateService.CheckAsync(force: false, CancellationToken.None);
        }
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        _ = InvokeAsync(() =>
        {
            Status = UpdateService.Status;
            StateHasChanged();
        });
    }

    private Task CheckNowAsync()
    {
        return UpdateService.CheckAsync(force: true, CancellationToken.None);
    }

    private void RequestInstall()
    {
        ShowInstallConfirm = true;
    }

    private void CancelInstall()
    {
        ShowInstallConfirm = false;
    }

    private async Task ConfirmInstallAsync()
    {
        ShowInstallConfirm = false;

        // L'avancement arrive par StatusChanged ; l'installation se poursuit même si la page se ferme.
        await UpdateService.InstallAsync(CancellationToken.None);
    }

    private async Task OnAutoCheckChanged(ChangeEventArgs e)
    {
        AutoCheck = e.Value is bool value && value;
        await SaveSettingsAsync();
    }

    private async Task OnIncludePreReleasesChanged(ChangeEventArgs e)
    {
        IncludePreReleases = e.Value is bool value && value;

        if (await SaveSettingsAsync())
        {
            // Le canal a changé : la version proposée peut être différente.
            await UpdateService.CheckAsync(force: true, CancellationToken.None);
        }
    }

    private async Task<bool> SaveSettingsAsync()
    {
        try
        {
            await SettingsStore.UpdateAsync(settings =>
            {
                settings.Updates.AutoCheck = AutoCheck;
                settings.Updates.IncludePreReleases = IncludePreReleases;
            });
            SettingsMessage = L["Préférences enregistrées."];
            return true;
        }
        catch (Exception ex)
        {
            SettingsMessage = L["Erreur lors de l'enregistrement : {0}", ex.Message];
            return false;
        }
    }

    private string FormatDate(DateTimeOffset? date)
    {
        return date is null ? "—" : date.Value.ToLocalTime().ToString(L["d MMMM yyyy 'à' HH:mm"], DisplayCulture);
    }

    private string FormatSize(long bytes)
    {
        return bytes <= 0 ? string.Empty : L["{0} Mo", (bytes / (1024.0 * 1024.0)).ToString("0.#", DisplayCulture)];
    }

    private string StateLabel => Status.State switch
    {
        UpdateState.Downloading => L["Téléchargement de l'archive…"],
        UpdateState.Verifying => L["Vérification de l'empreinte SHA-256…"],
        UpdateState.Installing => L["Installation des nouveaux fichiers…"],
        UpdateState.Restarting => L["Redémarrage d'OnyxFilter…"],
        _ => string.Empty,
    };

    public void Dispose()
    {
        UpdateService.StatusChanged -= OnStatusChanged;
    }
}
