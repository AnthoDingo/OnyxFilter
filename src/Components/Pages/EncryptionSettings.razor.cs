using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;

namespace OnyxFilter.Components.Pages;

public partial class EncryptionSettings : ComponentBase
{
    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

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

    protected override async Task OnInitializedAsync()
    {
        AppLocalSettings settings = await SettingsStore.LoadAsync();
        ApplyData(settings.Encryption);
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
    }

    private EncryptionSettingsData BuildData()
    {
        return new EncryptionSettingsData
        {
            EnableEncryption = EnableEncryption,
            EnablePlainDns = EnablePlainDns,
            ServerName = ServerName,
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
        };
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
        // TODO : valider la configuration (ports, certificats) une fois le moteur de chiffrement disponible.
        try
        {
            EncryptionSettingsData data = BuildData();
            await SettingsStore.UpdateAsync(settings => settings.Encryption = data);
            EncryptionStatusMessage = "Paramètres enregistrés.";
        }
        catch (Exception ex)
        {
            EncryptionStatusMessage = "Erreur lors de l'enregistrement : " + ex.Message;
        }
    }

    private Task ResetEncryptionSettingsAsync()
    {
        ApplyData(new EncryptionSettingsData());
        EncryptionStatusMessage = null;

        return Task.CompletedTask;
    }
}
