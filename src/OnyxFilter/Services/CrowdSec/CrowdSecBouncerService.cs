using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.DnsForwarding;
using OnyxFilter.Services.Updates;

namespace OnyxFilter.Services.CrowdSec;

public sealed record CrowdSecStatus(bool Enabled, bool Connected, int DecisionCount, DateTime? LastSyncUtc, string? LastError);

public interface ICrowdSecBouncer
{
    // Adresse bannie par une décision CrowdSec en cours (adresse seule ou plage).
    bool IsBanned(IPAddress address);

    // Même vérification, seulement si "Protéger aussi l'interface web" est coché.
    bool IsBannedFromWebInterface(IPAddress address);

    CrowdSecStatus Status { get; }

    // Vérifie l'adresse et la clé sans toucher au flux en cours. Null si la connexion réussit, sinon la
    // raison de l'échec.
    Task<string?> TestConnectionAsync(string lapiUrl, string apiKey, CancellationToken cancellationToken);
}

// Bouncer CrowdSec : interroge le flux des décisions de la LAPI (/v1/decisions/stream), la première fois
// en entier (startup=true), ensuite seulement les ajouts et suppressions. Les décisions d'adresse ("Ip")
// et de plage ("Range") sont gardées en mémoire ; les autres portées (pays, AS…) sont ignorées. Quel que
// soit leur type (ban, captcha…), elles sont appliquées comme un bannissement : le DNS ne sait pas
// présenter de captcha. LAPI injoignable : les décisions déjà connues restent appliquées.
public sealed class CrowdSecBouncerService : BackgroundService, ICrowdSecBouncer
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<CrowdSecBouncerService> logger;
    private readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim wakeSignal = new SemaphoreSlim(0);

    // Décisions en cours, par identifiant (le flux signale les suppressions par identifiant).
    private readonly Dictionary<long, string> decisionValues = new Dictionary<long, string>();

    private volatile BanSnapshot bans = BanSnapshot.Empty;
    private volatile CrowdSecStatus status = new CrowdSecStatus(false, false, 0, null, null);
    private volatile bool protectWebInterface;

    public CrowdSecBouncerService(ILocalSettingsStore settingsStore, ILogger<CrowdSecBouncerService> logger)
    {
        this.settingsStore = settingsStore;
        this.logger = logger;
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("onyxfilter-bouncer/" + AppVersion.Current);
        settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public CrowdSecStatus Status => status;

    public bool IsBanned(IPAddress address)
    {
        return bans.Contains(address);
    }

    public bool IsBannedFromWebInterface(IPAddress address)
    {
        return protectWebInterface && bans.Contains(address);
    }

    public async Task<string?> TestConnectionAsync(string lapiUrl, string apiKey, CancellationToken cancellationToken)
    {
        if (!TryBuildUri(lapiUrl, "v1/decisions?ip=127.0.0.1", out Uri? uri))
        {
            return "Adresse de l'API locale invalide (http:// ou https:// attendu).";
        }

        try
        {
            using HttpRequestMessage request = BuildRequest(uri, apiKey);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            return DescribeFailure(response);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ex.Message;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string appliedConfiguration = string.Empty;
        bool startup = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            CrowdSecSettingsData settings;

            try
            {
                settings = (await settingsStore.LoadAsync()).CrowdSec;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Impossible de lire les paramètres CrowdSec.");
                settings = new CrowdSecSettingsData();
            }

            protectWebInterface = settings.ProtectWebInterface;
            string configuration = settings.Enabled + "|" + settings.LapiUrl.Trim() + "|" + settings.ApiKey.Trim();

            // Nouvelle configuration (activation, adresse ou clé) : on repart d'un flux complet.
            if (configuration != appliedConfiguration)
            {
                appliedConfiguration = configuration;
                startup = true;
                ClearDecisions();
                status = new CrowdSecStatus(settings.Enabled, false, 0, null, null);
            }

            if (settings.Enabled)
            {
                startup = await PullAsync(settings, startup, stoppingToken) ? false : startup;
            }

            try
            {
                await wakeSignal.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(settings.PollIntervalSeconds, 1, 3600)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Retourne true si le flux a été lu et appliqué.
    private async Task<bool> PullAsync(CrowdSecSettingsData settings, bool startup, CancellationToken cancellationToken)
    {
        if (!TryBuildUri(settings.LapiUrl, "v1/decisions/stream?startup=" + (startup ? "true" : "false"), out Uri? uri))
        {
            status = status with { Connected = false, LastError = "Adresse de l'API locale invalide (http:// ou https:// attendu)." };
            return false;
        }

        try
        {
            using HttpRequestMessage request = BuildRequest(uri, settings.ApiKey);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            string? failure = DescribeFailure(response);

            if (failure is not null)
            {
                SetError(failure);
                return false;
            }

            DecisionStream? stream = await response.Content.ReadFromJsonAsync<DecisionStream>(JsonOptions, cancellationToken);
            ApplyStream(stream, startup);
            status = new CrowdSecStatus(true, true, bans.Count, DateTime.UtcNow, null);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            SetError(ex.Message);
            return false;
        }
    }

    private void ApplyStream(DecisionStream? stream, bool startup)
    {
        if (startup)
        {
            decisionValues.Clear();
        }

        foreach (Decision decision in stream?.Deleted ?? new List<Decision>())
        {
            decisionValues.Remove(decision.Id);
        }

        foreach (Decision decision in stream?.New ?? new List<Decision>())
        {
            if (decision.Value is not null
                && (string.Equals(decision.Scope, "Ip", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(decision.Scope, "Range", StringComparison.OrdinalIgnoreCase)))
            {
                decisionValues[decision.Id] = decision.Value;
            }
        }

        bans = BanSnapshot.Build(decisionValues.Values);
    }

    private void ClearDecisions()
    {
        decisionValues.Clear();
        bans = BanSnapshot.Empty;
    }

    private void SetError(string message)
    {
        if (status.LastError != message)
        {
            logger.LogWarning("CrowdSec : échec de la synchronisation des décisions ({Message}). Les décisions déjà connues restent appliquées.", message);
        }

        status = status with { Connected = false, LastError = message };
    }

    private static string? DescribeFailure(HttpResponseMessage response)
    {
        return response.StatusCode switch
        {
            HttpStatusCode.OK => null,
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "Clé du bouncer refusée par CrowdSec (cscli bouncers add onyxfilter).",
            _ => $"Réponse inattendue de CrowdSec : {(int)response.StatusCode} {response.ReasonPhrase}.",
        };
    }

    private static HttpRequestMessage BuildRequest(Uri uri, string apiKey)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey.Trim());
        return request;
    }

    private static bool TryBuildUri(string lapiUrl, string relativePath, out Uri? uri)
    {
        uri = null;

        if (!Uri.TryCreate(lapiUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = new Uri(baseUri, relativePath);
        return true;
    }

    private void OnSettingsChanged()
    {
        wakeSignal.Release();
    }

    public override void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
        httpClient.Dispose();
        wakeSignal.Dispose();
        base.Dispose();
    }

    private sealed record DecisionStream(List<Decision>? New, List<Decision>? Deleted);

    private sealed record Decision(long Id, string? Scope, string? Value);

    // Adresses bannies (recherche directe) et plages (parcours linéaire : peu nombreuses en pratique).
    // Instantané immuable, remplacé en bloc à chaque synchronisation.
    internal sealed class BanSnapshot
    {
        public static readonly BanSnapshot Empty = new BanSnapshot(new HashSet<IPAddress>(), Array.Empty<ClientAccessRule>());

        private readonly HashSet<IPAddress> addresses;
        private readonly ClientAccessRule[] ranges;

        private BanSnapshot(HashSet<IPAddress> addresses, ClientAccessRule[] ranges)
        {
            this.addresses = addresses;
            this.ranges = ranges;
        }

        public int Count => addresses.Count + ranges.Length;

        public static BanSnapshot Build(IEnumerable<string> values)
        {
            HashSet<IPAddress> addresses = new HashSet<IPAddress>();
            List<ClientAccessRule> ranges = new List<ClientAccessRule>();

            foreach (string value in values)
            {
                if (!value.Contains('/', StringComparison.Ordinal) && IPAddress.TryParse(value, out IPAddress? address))
                {
                    addresses.Add(Normalize(address));
                }
                else if (ClientAccessRule.TryParse(value, out ClientAccessRule rule))
                {
                    ranges.Add(rule);
                }
            }

            return new BanSnapshot(addresses, ranges.ToArray());
        }

        public bool Contains(IPAddress address)
        {
            if (Count == 0)
            {
                return false;
            }

            if (addresses.Contains(Normalize(address)))
            {
                return true;
            }

            foreach (ClientAccessRule range in ranges)
            {
                if (range.Matches(address))
                {
                    return true;
                }
            }

            return false;
        }

        private static IPAddress Normalize(IPAddress address)
        {
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }
    }
}
