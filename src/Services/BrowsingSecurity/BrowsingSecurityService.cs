using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.DnsForwarding;

namespace OnyxFilter.Services.BrowsingSecurity;

// Implémente "Utilisez le service Sécurité de navigation d'OnyxFilter" (Paramètres généraux) en
// s'appuyant sur AdGuardHashPrefixLookup avec le suffixe "sb.dns.adguard.com" (voir cette classe pour le
// détail du protocole repris d'AdGuard Home). Ne fait ici que le lien avec les réglages (activation,
// mode de blocage) et la construction de la réponse DNS de blocage.
public sealed class BrowsingSecurityService : IBrowsingSecurityService, IDisposable
{
    // Suffixe de question repris tel quel de l'implémentation d'AdGuard Home pour son service "Safe
    // Browsing" (distinct de "pc.dns.adguard.com", utilisé par le Contrôle parental).
    private const string QuestionSuffix = "sb.dns.adguard.com.";

    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<BrowsingSecurityService> logger;
    private readonly AdGuardHashPrefixLookup lookup;
    private readonly object syncRoot = new object();

    private bool enabled;
    private DnsBlockingMode blockingMode = DnsBlockingMode.Default;
    private string customBlockingIpv4 = string.Empty;
    private string customBlockingIpv6 = string.Empty;

    public BrowsingSecurityService(ILocalSettingsStore settingsStore, ILogger<BrowsingSecurityService> logger)
    {
        this.settingsStore = settingsStore;
        this.logger = logger;
        lookup = new AdGuardHashPrefixLookup(QuestionSuffix, logger);
        this.settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        AppLocalSettings settings = await settingsStore.LoadAsync();
        ApplySettingsSnapshot(settings);
    }

    public async Task<byte[]?> TryBuildBlockResponseAsync(byte[] query, CancellationToken cancellationToken)
    {
        bool isEnabled;
        DnsBlockingMode mode;
        string ipv4;
        string ipv6;

        lock (syncRoot)
        {
            isEnabled = enabled;
            mode = blockingMode;
            ipv4 = customBlockingIpv4;
            ipv6 = customBlockingIpv6;
        }

        if (!isEnabled)
        {
            return null;
        }

        if (!DnsMessageParser.TryReadQuestionName(query, out string name) || name.Length == 0)
        {
            return null;
        }

        if (!DnsMessageParser.TryReadQuestionType(query, out ushort queryType))
        {
            return null;
        }

        bool isBlocked;

        try
        {
            isBlocked = await lookup.IsHostBlockedAsync(name, cancellationToken);
        }
        catch (Exception ex)
        {
            // Échec ouvert ("fail open") : si le service de sécurité de navigation est injoignable ou
            // répond trop lentement, la requête n'est pas bloquée plutôt que de casser la résolution DNS
            // de tous les clients pour une panne d'un service tiers.
            logger.LogDebug(ex, "Vérification de sécurité de navigation impossible pour {Domain} : requête laissée passer.", name);
            return null;
        }

        return isBlocked ? DnsBlockResponseBuilder.Build(query, queryType, mode, ipv4, ipv6) : null;
    }

    private void OnSettingsChanged()
    {
        _ = ReloadSettingsAsync();
    }

    private async Task ReloadSettingsAsync()
    {
        try
        {
            AppLocalSettings settings = await settingsStore.LoadAsync();
            ApplySettingsSnapshot(settings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de recharger les réglages du service de sécurité de navigation.");
        }
    }

    private void ApplySettingsSnapshot(AppLocalSettings settings)
    {
        lock (syncRoot)
        {
            enabled = settings.General.UseBrowsingSecurity;
            blockingMode = settings.Dns.BlockingMode;
            customBlockingIpv4 = settings.Dns.CustomBlockingIpv4;
            customBlockingIpv6 = settings.Dns.CustomBlockingIpv6;
        }
    }

    public void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
        lookup.Dispose();
    }
}
