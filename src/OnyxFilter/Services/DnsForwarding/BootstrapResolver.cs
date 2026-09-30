using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.DnsForwarding;

// Résout les noms d'hôte des serveurs en amont (ex. "dns.cloudflare.com") en adresses IP, en utilisant
// exclusivement les "Serveurs DNS d'amorçage" configurés sur /settings/dns, plutôt que le résolveur du
// système d'exploitation (évite la dépendance circulaire : résoudre un serveur DoH avec le résolveur
// système avant même qu'un serveur DNS ne soit disponible). Les entrées déjà littérales (adresses IP)
// sont retournées immédiatement, sans aucune requête réseau.
public sealed class BootstrapResolver : IBootstrapResolver, IDisposable
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumCacheTtl = TimeSpan.FromSeconds(30);

    private sealed class CacheEntry
    {
        public CacheEntry(IPAddress address, DateTime expiresAtUtc)
        {
            Address = address;
            ExpiresAtUtc = expiresAtUtc;
        }

        public IPAddress Address { get; }

        public DateTime ExpiresAtUtc { get; }
    }

    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<BootstrapResolver> logger;
    private readonly object syncRoot = new object();
    private readonly Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>();

    private IReadOnlyList<IPAddress> bootstrapServers = Array.Empty<IPAddress>();

    public BootstrapResolver(ILocalSettingsStore settingsStore, ILogger<BootstrapResolver> logger)
    {
        this.settingsStore = settingsStore;
        this.logger = logger;
        this.settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync();
    }

    public async Task<IPAddress?> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            return literal;
        }

        lock (syncRoot)
        {
            if (cache.TryGetValue(host, out CacheEntry? cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            {
                return cached.Address;
            }
        }

        IReadOnlyList<IPAddress> servers;

        lock (syncRoot)
        {
            servers = bootstrapServers;
        }

        if (servers.Count == 0)
        {
            logger.LogWarning(
                "Impossible de résoudre '{Host}' : aucun serveur DNS d'amorçage configuré (voir « Serveurs DNS " +
                "d'amorçage » dans /settings/dns).",
                host);
            return null;
        }

        (IPAddress? address, int ttlSeconds) = await QueryBootstrapServersAsync(host, servers, cancellationToken);

        if (address is null)
        {
            logger.LogWarning("Échec de la résolution d'amorçage pour '{Host}' auprès de tous les serveurs configurés.", host);
            return null;
        }

        TimeSpan ttl = ttlSeconds > 0 ? TimeSpan.FromSeconds(ttlSeconds) : DefaultCacheTtl;

        if (ttl < MinimumCacheTtl)
        {
            ttl = MinimumCacheTtl;
        }

        lock (syncRoot)
        {
            cache[host] = new CacheEntry(address, DateTime.UtcNow.Add(ttl));
        }

        return address;
    }

    private async Task<(IPAddress? Address, int TtlSeconds)> QueryBootstrapServersAsync(
        string host,
        IReadOnlyList<IPAddress> servers,
        CancellationToken cancellationToken)
    {
        byte[] query = DnsQueryBuilder.BuildQuery(host, DnsMessageParser.TypeA, out ushort transactionId);

        foreach (IPAddress server in servers)
        {
            try
            {
                using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(QueryTimeout);

                using UdpClient client = new UdpClient(server.AddressFamily);
                await client.SendAsync(query, query.Length, new IPEndPoint(server, 53)).WaitAsync(timeoutSource.Token);

                UdpReceiveResult result = await client.ReceiveAsync().WaitAsync(timeoutSource.Token);

                if (DnsMessageParser.TryReadTransactionId(result.Buffer, out ushort responseId) && responseId == transactionId &&
                    DnsMessageParser.TryExtractFirstAddress(result.Buffer, DnsMessageParser.TypeA, out IPAddress? address, out int ttlSeconds))
                {
                    return (address, ttlSeconds);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Échec de la requête d'amorçage vers {Server} pour '{Host}'.", server, host);
            }
        }

        return (null, 0);
    }

    private void OnSettingsChanged()
    {
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            AppLocalSettings settings = await settingsStore.LoadAsync();
            List<IPAddress> parsed = new List<IPAddress>();

            foreach (string line in settings.Dns.BootstrapServers)
            {
                string trimmed = line.Trim();

                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (IPAddress.TryParse(trimmed, out IPAddress? address))
                {
                    parsed.Add(address);
                }
                else
                {
                    logger.LogWarning("Serveur DNS d'amorçage ignoré (adresse IP invalide) : '{Line}'.", trimmed);
                }
            }

            lock (syncRoot)
            {
                bootstrapServers = parsed;
                cache.Clear();
            }

            logger.LogInformation("{Count} serveur(s) DNS d'amorçage chargé(s).", parsed.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de charger les serveurs DNS d'amorçage depuis appsettings.local.json.");
        }
    }

    public void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
    }
}
