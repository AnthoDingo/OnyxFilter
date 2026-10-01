using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using OnyxFilter.Services.Encryption;

namespace OnyxFilter.Components.Pages;

public partial class EncryptionSettings : ComponentBase, IDisposable
{
    private const string AdditionalDomainsPlaceholder = "doh.exemple.fr\nautre.exemple.fr";

    private static CultureInfo DisplayCulture => CultureInfo.CurrentCulture;

    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

    [Inject]
    public ILetsEncryptService LetsEncryptService { get; set; } = default!;

    [Inject]
    public IHttpsEndpointService HttpsEndpointService { get; set; } = default!;

    [Inject]
    public NavigationManager Navigation { get; set; } = default!;

    private bool EnableEncryption { get; set; }

    private bool EnablePlainDns { get; set; } = true;

    private string ServerName { get; set; } = string.Empty;

    private bool AutoRedirectToHttps { get; set; }

    private int HttpsPort { get; set; } = 443;

    private int DnsOverTlsPort { get; set; } = 853;

    private int DnsOverQuicPort { get; set; } = 784;

    private CertificateInputSource CertificateSource { get; set; } = CertificateInputSource.FilePath;

    private string CertificateFilePath { get; set; } = string.Empty;

    private string CertificateContent { get; set; } = string.Empty;

    private CertificateInputSource PrivateKeySource { get; set; } = CertificateInputSource.FilePath;

    private string PrivateKeyFilePath { get; set; } = string.Empty;

    private string PrivateKeyContent { get; set; } = string.Empty;

    private string? EncryptionStatusMessage { get; set; }

    // Section « Certificat Let's Encrypt ».
    private bool LetsEncryptEnabled { get; set; }

    private string AdditionalDomainsText { get; set; } = string.Empty;

    private string LetsEncryptEmail { get; set; } = string.Empty;

    private bool LetsEncryptUseStaging { get; set; }

    private bool LetsEncryptAcceptTerms { get; set; }

    private int LetsEncryptChallengePort { get; set; } = 80;

    private string? LetsEncryptSaveMessage { get; set; }

    private LetsEncryptStatus LetsEncryptStatus { get; set; } = LetsEncryptStatus.Initial;

    private HttpsEndpointStatus HttpsStatus { get; set; } = HttpsEndpointStatus.Initial;

    // Adresse HTTPS de l'interface : nom du serveur, à défaut l'hôte de la page actuelle.
    private string HttpsBaseUrl
    {
        get
        {
            string host = string.IsNullOrWhiteSpace(SavedSettings.ServerName) ? new Uri(Navigation.BaseUri).Host : SavedSettings.ServerName;
            return HttpsStatus.Port == 443 ? $"https://{host}/" : $"https://{host}:{HttpsStatus.Port}/";
        }
    }

    // Paramètres enregistrés (et non ceux du formulaire en cours de saisie).
    private EncryptionSettingsData SavedSettings { get; set; } = new EncryptionSettingsData();

    // Certificat fourni à la main, tel que le chargeraient les services chiffrés.
    private CertificateSummary? ManualCertificate { get; set; }

    private string? ManualCertificateError { get; set; }

    private IReadOnlyList<string> LetsEncryptDomains => LetsEncryptPolicy.GetDomains(BuildData());

    private bool HasManualCertificateConfigured =>
        !string.IsNullOrWhiteSpace(SavedSettings.CertificateFilePath) || !string.IsNullOrWhiteSpace(SavedSettings.CertificateContent);

    protected override async Task OnInitializedAsync()
    {
        LetsEncryptStatus = LetsEncryptService.Status;
        LetsEncryptService.StatusChanged += OnLetsEncryptStatusChanged;
        HttpsStatus = HttpsEndpointService.Status;
        HttpsEndpointService.StatusChanged += OnHttpsStatusChanged;

        AppLocalSettings settings = await SettingsStore.LoadAsync();
        ApplyData(settings.Encryption);
        RefreshSavedState(settings.Encryption);
    }

    private void ApplyData(EncryptionSettingsData data)
    {
        EnableEncryption = data.EnableEncryption;
        EnablePlainDns = data.EnablePlainDns;
        ServerName = data.ServerName;
        AutoRedirectToHttps = data.AutoRedirectToHttps;
        HttpsPort = data.HttpsPort;
        DnsOverTlsPort = data.DnsOverTlsPort;
        DnsOverQuicPort = data.DnsOverQuicPort;
        CertificateSource = data.CertificateSource;
        CertificateFilePath = data.CertificateFilePath;
        CertificateContent = data.CertificateContent;
        PrivateKeySource = data.PrivateKeySource;
        PrivateKeyFilePath = data.PrivateKeyFilePath;
        PrivateKeyContent = data.PrivateKeyContent;
        LetsEncryptEnabled = data.LetsEncrypt.Enabled;
        AdditionalDomainsText = string.Join("\n", data.LetsEncrypt.AdditionalDomains);
        LetsEncryptEmail = data.LetsEncrypt.Email;
        LetsEncryptUseStaging = data.LetsEncrypt.UseStaging;
        LetsEncryptAcceptTerms = data.LetsEncrypt.AcceptTermsOfService;
        LetsEncryptChallengePort = data.LetsEncrypt.ChallengePort;
    }

    private EncryptionSettingsData BuildData()
    {
        return new EncryptionSettingsData
        {
            EnableEncryption = EnableEncryption,
            EnablePlainDns = EnablePlainDns,
            ServerName = ServerName.Trim(),
            AutoRedirectToHttps = AutoRedirectToHttps,
            HttpsPort = HttpsPort,
            DnsOverTlsPort = DnsOverTlsPort,
            DnsOverQuicPort = DnsOverQuicPort,
            CertificateSource = CertificateSource,
            CertificateFilePath = CertificateFilePath,
            CertificateContent = CertificateContent,
            PrivateKeySource = PrivateKeySource,
            PrivateKeyFilePath = PrivateKeyFilePath,
            PrivateKeyContent = PrivateKeyContent,
            LetsEncrypt = new LetsEncryptSettingsData
            {
                Enabled = LetsEncryptEnabled,
                AdditionalDomains = AdditionalDomainsText
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray(),
                Email = LetsEncryptEmail.Trim(),
                UseStaging = LetsEncryptUseStaging,
                AcceptTermsOfService = LetsEncryptAcceptTerms,
                ChallengePort = LetsEncryptChallengePort,
            },
        };
    }

    // Après chargement ou enregistrement : état enregistré, et vérification du certificat fourni à la main.
    private void RefreshSavedState(EncryptionSettingsData saved)
    {
        SavedSettings = saved;
        ManualCertificate = null;
        ManualCertificateError = null;

        if (saved.LetsEncrypt.Enabled || !HasManualCertificateConfigured)
        {
            return;
        }

        EncryptionSettingsData manual = new EncryptionSettingsData
        {
            CertificateSource = saved.CertificateSource,
            CertificateFilePath = saved.CertificateFilePath,
            CertificateContent = saved.CertificateContent,
            PrivateKeySource = saved.PrivateKeySource,
            PrivateKeyFilePath = saved.PrivateKeyFilePath,
            PrivateKeyContent = saved.PrivateKeyContent,
        };

        try
        {
            using LoadedServerCertificate loaded = ServerCertificateLoader.Load(manual);
            ManualCertificate = loaded.Summary;
        }
        catch (InvalidOperationException ex)
        {
            ManualCertificateError = ex.Message;
        }
    }

    private void OnEnableEncryptionChanged(ChangeEventArgs e)
    {
        EnableEncryption = e.Value is bool value && value;
    }

    private void OnEnablePlainDnsChanged(ChangeEventArgs e)
    {
        EnablePlainDns = e.Value is bool value && value;
    }

    private void OnAutoRedirectToHttpsChanged(ChangeEventArgs e)
    {
        AutoRedirectToHttps = e.Value is bool value && value;
    }

    private void OnLetsEncryptEnabledChanged(ChangeEventArgs e)
    {
        LetsEncryptEnabled = e.Value is bool value && value;
    }

    private void OnLetsEncryptAcceptTermsChanged(ChangeEventArgs e)
    {
        LetsEncryptAcceptTerms = e.Value is bool value && value;
    }

    private void SetLetsEncryptStaging(bool useStaging)
    {
        LetsEncryptUseStaging = useStaging;
    }

    private void SetCertificateSource(CertificateInputSource source)
    {
        CertificateSource = source;
    }

    private void SetPrivateKeySource(CertificateInputSource source)
    {
        PrivateKeySource = source;
    }

    private async Task SaveEncryptionConfigurationAsync()
    {
        EncryptionStatusMessage = await SaveAsync();
    }

    private async Task SaveLetsEncryptConfigurationAsync()
    {
        LetsEncryptSaveMessage = await SaveAsync();
    }

    // Enregistre tout le formulaire ; le service Let's Encrypt réagit aussitôt à un changement de sa
    // configuration (obtention du certificat si nécessaire).
    private async Task<string> SaveAsync()
    {
        try
        {
            EncryptionSettingsData data = BuildData();
            EncryptionSettingsData? saved = null;

            await SettingsStore.UpdateAsync(settings =>
            {
                // Empreinte tenue par le service Let's Encrypt : à conserver.
                data.LetsEncrypt.CertificateThumbprint = settings.Encryption.LetsEncrypt.CertificateThumbprint;
                settings.Encryption = data;
                saved = data;
            });

            RefreshSavedState(saved ?? data);
            return L["Paramètres enregistrés."];
        }
        catch (Exception ex)
        {
            return L["Erreur lors de l'enregistrement : {0}", ex.Message];
        }
    }

    private Task ResetEncryptionSettingsAsync()
    {
        ApplyData(new EncryptionSettingsData());
        EncryptionStatusMessage = null;

        return Task.CompletedTask;
    }

    private void RenewNow()
    {
        LetsEncryptService.RequestCheck(forceRenewal: true);
    }

    private void OnLetsEncryptStatusChanged()
    {
        _ = InvokeAsync(() =>
        {
            LetsEncryptStatus = LetsEncryptService.Status;
            StateHasChanged();
        });
    }

    private void OnHttpsStatusChanged()
    {
        _ = InvokeAsync(() =>
        {
            HttpsStatus = HttpsEndpointService.Status;
            StateHasChanged();
        });
    }

    private string FormatDate(DateTime? utc)
    {
        return utc is null ? "—" : utc.Value.ToLocalTime().ToString(L["d MMMM yyyy 'à' HH:mm"], DisplayCulture);
    }

    private static string FormatDay(DateTime utc)
    {
        return utc.ToLocalTime().ToString("d MMMM yyyy", DisplayCulture);
    }

    // « dans 74 jours », « dans 5 heures », « expiré ».
    private string FormatRemaining(DateTime utc)
    {
        TimeSpan remaining = utc - DateTime.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            return L["expiré"];
        }

        if (remaining.TotalDays >= 2)
        {
            return L["dans {0} jours", (int)remaining.TotalDays];
        }

        return remaining.TotalHours >= 2 ? L["dans {0} heures", (int)remaining.TotalHours] : L["dans moins de 2 heures"];
    }

    public void Dispose()
    {
        LetsEncryptService.StatusChanged -= OnLetsEncryptStatusChanged;
        HttpsEndpointService.StatusChanged -= OnHttpsStatusChanged;
    }
}
