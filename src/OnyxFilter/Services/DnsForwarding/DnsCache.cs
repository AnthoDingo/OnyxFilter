using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.DnsForwarding;

// Cache DNS en mémoire, borné par une taille en octets (paramètre "Taille du cache" de /settings/dns).
// Lorsque le cache est plein, les entrées les plus anciennes (par ordre d'insertion) sont supprimées
// pour faire de la place aux nouvelles réponses.
public sealed class DnsCache : IDnsCache, IDisposable
{
    // Utilisée quand CacheSizeBytes vaut 0 ("laisser OnyxFilter choisir automatiquement une taille par
    // défaut"). Volontairement modeste : l'application doit rester légère sur un Raspberry Pi 3 (2 Go de RAM).
    private const long DefaultMaxSizeBytes = 4L * 1024 * 1024;

    // TTL servi aux clients pour une réponse expirée en mode cache optimiste : assez court pour que le
    // client redemande rapidement (et obtienne alors la réponse rafraîchie en arrière-plan).
    private const int OptimisticStaleTtlSeconds = 10;

    private sealed class Entry
    {
        public Entry(string key, byte[] response, DateTime expiresAtUtc)
        {
            Key = key;
            Response = response;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string Key { get; }

        public byte[] Response { get; }

        public DateTime ExpiresAtUtc { get; }

        public long SizeInBytes => Response.LongLength;
    }

    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<DnsCache> logger;
    private readonly object syncRoot = new object();
    private readonly Dictionary<string, LinkedListNode<Entry>> entriesByKey = new Dictionary<string, LinkedListNode<Entry>>();
    private readonly LinkedList<Entry> entriesByInsertionOrder = new LinkedList<Entry>();

    private long currentSizeBytes;
    private long maxSizeBytes = DefaultMaxSizeBytes;
    private int minTtlSeconds;
    private int maxTtlSeconds;
    private bool isEnabled;
    private bool optimisticCache;

    public DnsCache(ILocalSettingsStore settingsStore, ILogger<DnsCache> logger)
    {
        this.settingsStore = settingsStore;
        this.logger = logger;
        this.settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync()
    {
        await ReloadConfigurationAsync();
    }

    public byte[]? TryGet(string cacheKey, out bool isStale)
    {
        isStale = false;

        lock (syncRoot)
        {
            if (!isEnabled)
            {
                return null;
            }

            if (!entriesByKey.TryGetValue(cacheKey, out LinkedListNode<Entry>? node))
            {
                return null;
            }

            TimeSpan remaining = node.Value.ExpiresAtUtc - DateTime.UtcNow;

            if (remaining <= TimeSpan.Zero)
            {
                // Cache optimiste : l'entrée expirée est servie avec un TTL court, en attendant que
                // l'appelant la rafraîchisse en arrière-plan. Elle reste en cache (bornée par la
                // taille maximale) jusqu'à son remplacement ou son éviction.
                if (!optimisticCache)
                {
                    RemoveNode(node);
                    return null;
                }

                isStale = true;
                byte[] staleResponse = (byte[])node.Value.Response.Clone();
                DnsMessageParser.TryOverwriteTtls(staleResponse, OptimisticStaleTtlSeconds);
                return staleResponse;
            }

            byte[] response = (byte[])node.Value.Response.Clone();

            // Sert le TTL restant (au moins 1 seconde) plutôt que le TTL d'origine, pour que les
            // clients n'allongent pas la durée de vie réelle en re-cachant la valeur pleine.
            int remainingSeconds = Math.Max(1, (int)remaining.TotalSeconds);
            DnsMessageParser.TryOverwriteTtls(response, remainingSeconds);

            return response;
        }
    }

    public void Set(string cacheKey, byte[] response, TimeSpan ttl)
    {
        lock (syncRoot)
        {
            if (!isEnabled)
            {
                return;
            }

            TimeSpan clampedTtl = ClampTtl(ttl);

            if (clampedTtl <= TimeSpan.Zero)
            {
                return;
            }

            if (entriesByKey.TryGetValue(cacheKey, out LinkedListNode<Entry>? existing))
            {
                RemoveNode(existing);
            }

            // Copie défensive : la réponse est aussi envoyée telle quelle au premier client, elle ne
            // doit pas être mutée. Les TTL des enregistrements stockés sont alignés sur la durée de
            // rétention effective (bornes min/max appliquées).
            byte[] storedResponse = (byte[])response.Clone();
            DnsMessageParser.TryOverwriteTtls(storedResponse, (int)clampedTtl.TotalSeconds);

            Entry entry = new Entry(cacheKey, storedResponse, DateTime.UtcNow.Add(clampedTtl));

            EvictUntilFits(entry.SizeInBytes);

            LinkedListNode<Entry> node = entriesByInsertionOrder.AddLast(entry);
            entriesByKey[cacheKey] = node;
            currentSizeBytes += entry.SizeInBytes;
        }
    }

    public void Clear()
    {
        int removedCount;

        lock (syncRoot)
        {
            removedCount = entriesByKey.Count;
            entriesByKey.Clear();
            entriesByInsertionOrder.Clear();
            currentSizeBytes = 0;
        }

        logger.LogInformation("Cache DNS vidé ({Count} entrée(s) supprimée(s)).", removedCount);
    }

    private TimeSpan ClampTtl(TimeSpan ttl)
    {
        TimeSpan result = ttl;

        if (minTtlSeconds > 0 && result.TotalSeconds < minTtlSeconds)
        {
            result = TimeSpan.FromSeconds(minTtlSeconds);
        }

        if (maxTtlSeconds > 0 && result.TotalSeconds > maxTtlSeconds)
        {
            result = TimeSpan.FromSeconds(maxTtlSeconds);
        }

        return result;
    }

    private void EvictUntilFits(long incomingSize)
    {
        while (entriesByInsertionOrder.Count > 0 && currentSizeBytes + incomingSize > maxSizeBytes)
        {
            LinkedListNode<Entry> oldest = entriesByInsertionOrder.First!;
            RemoveNode(oldest);
        }
    }

    private void RemoveNode(LinkedListNode<Entry> node)
    {
        entriesByKey.Remove(node.Value.Key);
        entriesByInsertionOrder.Remove(node);
        currentSizeBytes -= node.Value.SizeInBytes;
    }

    private void OnSettingsChanged()
    {
        _ = ReloadConfigurationAsync();
    }

    private async Task ReloadConfigurationAsync()
    {
        try
        {
            AppLocalSettings settings = await settingsStore.LoadAsync();

            lock (syncRoot)
            {
                isEnabled = settings.Dns.EnableCache;
                maxSizeBytes = settings.Dns.CacheSizeBytes > 0 ? settings.Dns.CacheSizeBytes : DefaultMaxSizeBytes;
                minTtlSeconds = settings.Dns.CacheTtlMinSeconds;
                maxTtlSeconds = settings.Dns.CacheTtlMaxSeconds;
                optimisticCache = settings.Dns.OptimisticCache;

                if (!isEnabled)
                {
                    entriesByKey.Clear();
                    entriesByInsertionOrder.Clear();
                    currentSizeBytes = 0;
                }
                else
                {
                    EvictUntilFits(0);
                }
            }

            logger.LogInformation(
                "Configuration du cache DNS rechargée : activé={Enabled}, taille max={MaxSize} octets.",
                isEnabled,
                maxSizeBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de charger la configuration du cache DNS.");
        }
    }

    public void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
    }
}
