namespace OnyxFilter.Models.Settings;

// Reflète la page "CrowdSec" (/settings/crowdsec) : OnyxFilter comme composant de remédiation (bouncer)
// d'une instance CrowdSec. Les adresses bannies par CrowdSec ne sont plus servies par le DNS et, si
// ProtectWebInterface est coché, n'accèdent plus à l'interface web ni à l'API.
public sealed class CrowdSecSettingsData
{
    public bool Enabled { get; set; }

    // API locale (LAPI) de CrowdSec, ex. "http://127.0.0.1:8080".
    public string LapiUrl { get; set; } = "http://127.0.0.1:8080";

    // Clé du bouncer, créée par "cscli bouncers add onyxfilter".
    public string ApiKey { get; set; } = string.Empty;

    // Intervalle d'interrogation de la LAPI, en secondes (10 s comme les bouncers officiels).
    public int PollIntervalSeconds { get; set; } = 10;

    public bool ProtectWebInterface { get; set; } = true;
}
