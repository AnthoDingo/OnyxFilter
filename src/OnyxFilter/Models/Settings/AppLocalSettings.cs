namespace OnyxFilter.Models.Settings;

// Racine du fichier appsettings.local.json : un bloc par page de paramètres.
public sealed class AppLocalSettings
{
    public GeneralSettingsData General { get; set; } = new GeneralSettingsData();

    public DnsSettingsData Dns { get; set; } = new DnsSettingsData();

    public EncryptionSettingsData Encryption { get; set; } = new EncryptionSettingsData();

    public FilterListsSettingsData FilterLists { get; set; } = new FilterListsSettingsData();

    public AllowlistSettingsData Allowlist { get; set; } = new AllowlistSettingsData();

    public RewriteRulesSettingsData Rewrites { get; set; } = new RewriteRulesSettingsData();

    public CustomFilterRulesSettingsData CustomFilterRules { get; set; } = new CustomFilterRulesSettingsData();

    public BlockedServicesSettingsData BlockedServices { get; set; } = new BlockedServicesSettingsData();

    public ClientsSettingsData Clients { get; set; } = new ClientsSettingsData();

    public ApiSettingsData Api { get; set; } = new ApiSettingsData();
}
