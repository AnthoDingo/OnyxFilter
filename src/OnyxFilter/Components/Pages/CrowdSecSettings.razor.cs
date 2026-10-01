using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using OnyxFilter.Services.CrowdSec;

namespace OnyxFilter.Components.Pages;

public partial class CrowdSecSettings : ComponentBase, IDisposable
{
    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

    [Inject]
    public ICrowdSecBouncer Bouncer { get; set; } = default!;

    private bool Enabled { get; set; }

    private string LapiUrl { get; set; } = string.Empty;

    // Jamais renvoyée au navigateur : champ vide = clé enregistrée conservée.
    private string ApiKey { get; set; } = string.Empty;

    private bool HasSavedKey { get; set; }

    private int PollIntervalSeconds { get; set; }

    private bool ProtectWebInterface { get; set; }

    private CrowdSecStatus Status { get; set; } = new CrowdSecStatus(false, false, 0, null, null);

    private string? StatusMessage { get; set; }

    private bool StatusIsError { get; set; }

    private bool IsSaving { get; set; }

    private bool IsTesting { get; set; }

    // Rafraîchit l'état de la synchronisation affiché sur la page.
    private Timer? statusTimer;

    protected override async Task OnInitializedAsync()
    {
        CrowdSecSettingsData settings = (await SettingsStore.LoadAsync()).CrowdSec;
        Enabled = settings.Enabled;
        LapiUrl = settings.LapiUrl;
        HasSavedKey = settings.ApiKey.Length != 0;
        PollIntervalSeconds = settings.PollIntervalSeconds;
        ProtectWebInterface = settings.ProtectWebInterface;
        Status = Bouncer.Status;

        statusTimer = new Timer(_ => InvokeAsync(() =>
        {
            Status = Bouncer.Status;
            StateHasChanged();
        }), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private string? Validate()
    {
        if (!Uri.TryCreate(LapiUrl.Trim(), UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return L["Adresse de l'API locale invalide (http:// ou https:// attendu)."];
        }

        if (Enabled && !HasSavedKey && string.IsNullOrWhiteSpace(ApiKey))
        {
            return L["Renseignez la clé du bouncer."];
        }

        if (PollIntervalSeconds < 1 || PollIntervalSeconds > 3600)
        {
            return L["L'actualisation doit être comprise entre 1 et 3600 secondes."];
        }

        return null;
    }

    private async Task TestAsync()
    {
        string? error = Validate();

        if (error is null && !HasSavedKey && string.IsNullOrWhiteSpace(ApiKey))
        {
            error = L["Renseignez la clé du bouncer."];
        }

        if (error is not null)
        {
            SetStatus(error, isError: true);
            return;
        }

        IsTesting = true;

        try
        {
            string key = string.IsNullOrWhiteSpace(ApiKey) ? (await SettingsStore.LoadAsync()).CrowdSec.ApiKey : ApiKey;
            string? failure = await Bouncer.TestConnectionAsync(LapiUrl, key, CancellationToken.None);
            SetStatus(failure is null ? L["Connexion à CrowdSec réussie."] : L["Échec de la connexion à CrowdSec : {0}", failure], failure is not null);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private async Task SaveAsync()
    {
        string? error = Validate();

        if (error is not null)
        {
            SetStatus(error, isError: true);
            return;
        }

        bool enabled = Enabled;
        string lapiUrl = LapiUrl.Trim();
        string? newKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
        int interval = PollIntervalSeconds;
        bool protectWeb = ProtectWebInterface;
        IsSaving = true;

        try
        {
            await SettingsStore.UpdateAsync(settings =>
            {
                settings.CrowdSec.Enabled = enabled;
                settings.CrowdSec.LapiUrl = lapiUrl;
                settings.CrowdSec.PollIntervalSeconds = interval;
                settings.CrowdSec.ProtectWebInterface = protectWeb;

                if (newKey is not null)
                {
                    settings.CrowdSec.ApiKey = newKey;
                }
            });

            HasSavedKey |= newKey is not null;
            ApiKey = string.Empty;
            SetStatus(L["Paramètres CrowdSec enregistrés."], isError: false);
        }
        catch (Exception ex)
        {
            SetStatus(L["Erreur lors de l'enregistrement : {0}", ex.Message], isError: true);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    public void Dispose()
    {
        statusTimer?.Dispose();
    }
}
