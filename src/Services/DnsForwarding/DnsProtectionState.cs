using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace OnyxFilter.Services.DnsForwarding;

// Implémentation en mémoire de IDnsProtectionState : pas de persistance (redémarrer le serveur réactive
// toujours la protection), volontairement, pour ne jamais démarrer avec le filtrage silencieusement
// coupé. Un Timer programme la réactivation automatique pour les options avec échéance ("Pendant 30
// secondes", "Jusqu'à demain", etc.), plutôt que de vérifier l'heure à chaque requête DNS (chemin chaud,
// des dizaines de requêtes par seconde possibles).
public sealed class DnsProtectionState : IDnsProtectionState, IDisposable
{
    private readonly ILogger<DnsProtectionState> logger;
    private readonly object syncRoot = new object();

    // Lu sans verrou par IDnsQueryPipeline.ResolveAsync à chaque requête : doit rester une lecture
    // atomique bon marché.
    private volatile bool isEnabled = true;

    private DateTime? disabledUntilUtc;
    private Timer? reEnableTimer;

    public DnsProtectionState(ILogger<DnsProtectionState> logger)
    {
        this.logger = logger;
    }

    public bool IsEnabled => isEnabled;

    public DateTime? DisabledUntilUtc
    {
        get
        {
            lock (syncRoot)
            {
                return disabledUntilUtc;
            }
        }
    }

    public void Disable()
    {
        lock (syncRoot)
        {
            CancelPendingReEnable_NoLock();
            disabledUntilUtc = null;
            isEnabled = false;
        }

        logger.LogInformation("Protection DNS désactivée manuellement (sans échéance).");
    }

    public void DisableUntil(DateTime untilUtc)
    {
        DateTime untilUtcNormalized = untilUtc.Kind == DateTimeKind.Utc ? untilUtc : untilUtc.ToUniversalTime();
        TimeSpan delay = untilUtcNormalized - DateTime.UtcNow;

        lock (syncRoot)
        {
            CancelPendingReEnable_NoLock();

            if (delay <= TimeSpan.Zero)
            {
                // Échéance déjà passée (horloge, décalage réseau...) : rien à désactiver.
                disabledUntilUtc = null;
                isEnabled = true;
                return;
            }

            disabledUntilUtc = untilUtcNormalized;
            isEnabled = false;
            reEnableTimer = new Timer(OnReEnableTimer, null, delay, Timeout.InfiniteTimeSpan);
        }

        logger.LogInformation("Protection DNS désactivée jusqu'à {UntilUtc:O}.", untilUtcNormalized);
    }

    public void Enable()
    {
        lock (syncRoot)
        {
            CancelPendingReEnable_NoLock();
            disabledUntilUtc = null;
            isEnabled = true;
        }

        logger.LogInformation("Protection DNS réactivée.");
    }

    private void OnReEnableTimer(object? state)
    {
        lock (syncRoot)
        {
            // Le Timer a pu être annulé (nouvel appel Disable/DisableUntil/Enable) juste avant ce tick :
            // on ne réactive que si l'échéance en cours est bien celle qui a déclenché ce Timer.
            if (reEnableTimer is null)
            {
                return;
            }

            disabledUntilUtc = null;
            isEnabled = true;
            reEnableTimer.Dispose();
            reEnableTimer = null;
        }

        logger.LogInformation("Protection DNS réactivée automatiquement (échéance atteinte).");
    }

    private void CancelPendingReEnable_NoLock()
    {
        reEnableTimer?.Dispose();
        reEnableTimer = null;
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            CancelPendingReEnable_NoLock();
        }
    }
}
