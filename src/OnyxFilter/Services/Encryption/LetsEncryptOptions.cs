namespace OnyxFilter.Services.Encryption;

// Section « LetsEncrypt » d'appsettings.json : annuaires ACME utilisés. Les valeurs par défaut sont celles de
// Let's Encrypt ; elles ne se modifient qu'à la main (autre autorité ACME sans compte externe, serveur de test).
public sealed class LetsEncryptOptions
{
    public const string SectionName = "LetsEncrypt";

    public string ProductionDirectoryUrl { get; set; } = "https://acme-v02.api.letsencrypt.org/directory";

    public string StagingDirectoryUrl { get; set; } = "https://acme-staging-v02.api.letsencrypt.org/directory";
}
