using System.Threading.Tasks;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.DnsForwarding;

// Cache en mémoire du réglage "Activer le chiffrement" (EnableEncryption, /settings/encryption)
// pour l'endpoint DNS-over-HTTPS : évite une lecture disque d'appsettings.local.json à chaque
// requête DNS. Invalidé par SettingsChanged, donc rechargé à la première requête suivant chaque
// enregistrement de la page.
public sealed class DnsOverHttpsAvailability : IDnsOverHttpsAvailability
{
    private readonly ILocalSettingsStore settingsStore;

    // "volatile" suffit ici : en cas de course entre deux requêtes, chacune relit simplement les
    // paramètres, sans autre effet de bord.
    private volatile bool isLoaded;
    private volatile bool isEnabled;

    public DnsOverHttpsAvailability(ILocalSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore;
        settingsStore.SettingsChanged += () => isLoaded = false;
    }

    public async ValueTask<bool> IsEnabledAsync()
    {
        if (!isLoaded)
        {
            AppLocalSettings settings = await settingsStore.LoadAsync();
            isEnabled = settings.Encryption.EnableEncryption;
            isLoaded = true;
        }

        return isEnabled;
    }
}
