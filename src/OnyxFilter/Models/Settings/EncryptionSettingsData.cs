namespace OnyxFilter.Models.Settings;

// Reflète les champs de la page "Paramètres de chiffrement" (/settings/encryption).
public sealed class EncryptionSettingsData
{
    public bool EnableEncryption { get; set; }

    public bool EnablePlainDns { get; set; } = true;

    public string ServerName { get; set; } = string.Empty;

    public bool AutoRedirectToHttps { get; set; }

    public int HttpsPort { get; set; } = 443;

    public int DnsOverTlsPort { get; set; } = 853;

    public int DnsOverQuicPort { get; set; } = 784;

    public CertificateInputSource CertificateSource { get; set; } = CertificateInputSource.FilePath;

    public string CertificateFilePath { get; set; } = string.Empty;

    public string CertificateContent { get; set; } = string.Empty;

    public CertificateInputSource PrivateKeySource { get; set; } = CertificateInputSource.FilePath;

    public string PrivateKeyFilePath { get; set; } = string.Empty;

    public string PrivateKeyContent { get; set; } = string.Empty;

    // Activé, il remplace le certificat et la clé ci-dessus.
    public LetsEncryptSettingsData LetsEncrypt { get; set; } = new LetsEncryptSettingsData();
}
