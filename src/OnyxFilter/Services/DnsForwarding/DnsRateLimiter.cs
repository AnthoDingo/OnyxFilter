using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.DnsForwarding;

// Compteur à fenêtre fixe d'une seconde, par sous-réseau client. Conçu pour rester léger : une
// entrée par sous-réseau actif, clé compacte de 16 octets, purge périodique des entrées inactives.
public sealed class DnsRateLimiter : IDnsRateLimiter, IDisposable
{
    // Intervalle minimal entre deux purges des sous-réseaux inactifs.
    private const long SweepIntervalSeconds = 30;

    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<DnsRateLimiter> logger;
    private readonly object syncRoot = new object();
    private readonly Dictionary<SubnetKey, CounterEntry> counters = new Dictionary<SubnetKey, CounterEntry>();

    private int rateLimitPerSecond;
    private int subnetLengthIpv4 = 24;
    private int subnetLengthIpv6 = 56;
    private long lastSweepSecond;

    public DnsRateLimiter(ILocalSettingsStore settingsStore, ILogger<DnsRateLimiter> logger)
    {
        this.settingsStore = settingsStore;
        this.logger = logger;
        this.settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync()
    {
        await ReloadAsync();
    }

    public bool IsAllowed(IPAddress clientAddress)
    {
        int limit;
        int prefixIpv4;
        int prefixIpv6;

        lock (syncRoot)
        {
            limit = rateLimitPerSecond;
            prefixIpv4 = subnetLengthIpv4;
            prefixIpv6 = subnetLengthIpv6;
        }

        if (limit <= 0)
        {
            return true;
        }

        SubnetKey key = BuildSubnetKey(clientAddress, prefixIpv4, prefixIpv6);
        long currentSecond = Environment.TickCount64 / 1000;

        lock (syncRoot)
        {
            SweepIfDue(currentSecond);

            if (!counters.TryGetValue(key, out CounterEntry? entry))
            {
                entry = new CounterEntry();
                counters[key] = entry;
            }

            if (entry.WindowSecond != currentSecond)
            {
                entry.WindowSecond = currentSecond;
                entry.Count = 0;
            }

            entry.Count++;
            return entry.Count <= limit;
        }
    }

    public void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
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
            int limit = Math.Max(0, settings.Dns.RateLimitPerSecond);
            int prefixIpv4 = Math.Clamp(settings.Dns.RateLimitSubnetLengthIpv4, 0, 32);
            int prefixIpv6 = Math.Clamp(settings.Dns.RateLimitSubnetLengthIpv6, 0, 128);

            lock (syncRoot)
            {
                rateLimitPerSecond = limit;
                subnetLengthIpv4 = prefixIpv4;
                subnetLengthIpv6 = prefixIpv6;
                counters.Clear();
            }

            if (limit > 0)
            {
                logger.LogInformation(
                    "Limite de requêtes DNS : {Limit} requête(s)/seconde par sous-réseau (IPv4 /{PrefixIpv4}, IPv6 /{PrefixIpv6}).",
                    limit,
                    prefixIpv4,
                    prefixIpv6);
            }
            else
            {
                logger.LogInformation("Limite de requêtes DNS désactivée.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de charger la limite de requêtes DNS depuis appsettings.local.json.");
        }
    }

    // Doit être appelé sous le verrou. Supprime les sous-réseaux inactifs pour borner la mémoire.
    private void SweepIfDue(long currentSecond)
    {
        if (currentSecond - lastSweepSecond < SweepIntervalSeconds)
        {
            return;
        }

        lastSweepSecond = currentSecond;
        List<SubnetKey>? staleKeys = null;

        foreach (KeyValuePair<SubnetKey, CounterEntry> pair in counters)
        {
            if (pair.Value.WindowSecond != currentSecond)
            {
                staleKeys ??= new List<SubnetKey>();
                staleKeys.Add(pair.Key);
            }
        }

        if (staleKeys is not null)
        {
            foreach (SubnetKey staleKey in staleKeys)
            {
                counters.Remove(staleKey);
            }
        }
    }

    // Construit une clé de 128 bits représentant le sous-réseau du client : les adresses IPv4 (et les
    // IPv6 mappées IPv4, reçues via une socket dual-stack) utilisent le préfixe IPv4, les autres le
    // préfixe IPv6. Aucune allocation sur le tas.
    private static SubnetKey BuildSubnetKey(IPAddress clientAddress, int prefixIpv4, int prefixIpv6)
    {
        Span<byte> bytes = stackalloc byte[16];
        int totalPrefixLength;

        if (clientAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes.Clear();
            bytes[10] = 0xFF;
            bytes[11] = 0xFF;
            clientAddress.TryWriteBytes(bytes.Slice(12), out int _);
            totalPrefixLength = 96 + prefixIpv4;
        }
        else
        {
            clientAddress.TryWriteBytes(bytes, out int _);
            totalPrefixLength = clientAddress.IsIPv4MappedToIPv6 ? 96 + prefixIpv4 : prefixIpv6;
        }

        ulong high = ReadUInt64BigEndian(bytes, 0);
        ulong low = ReadUInt64BigEndian(bytes, 8);

        if (totalPrefixLength <= 0)
        {
            high = 0;
            low = 0;
        }
        else if (totalPrefixLength < 64)
        {
            high &= ulong.MaxValue << (64 - totalPrefixLength);
            low = 0;
        }
        else if (totalPrefixLength == 64)
        {
            // Cas à part : "ulong.MaxValue << 64" ne vaut pas 0 en C# (le décalage est pris modulo 64).
            low = 0;
        }
        else if (totalPrefixLength < 128)
        {
            low &= ulong.MaxValue << (128 - totalPrefixLength);
        }

        return new SubnetKey(high, low);
    }

    private static ulong ReadUInt64BigEndian(Span<byte> bytes, int offset)
    {
        ulong value = 0;

        for (int index = 0; index < 8; index++)
        {
            value = (value << 8) | bytes[offset + index];
        }

        return value;
    }

    private readonly struct SubnetKey : IEquatable<SubnetKey>
    {
        private readonly ulong high;
        private readonly ulong low;

        public SubnetKey(ulong high, ulong low)
        {
            this.high = high;
            this.low = low;
        }

        public bool Equals(SubnetKey other)
        {
            return high == other.high && low == other.low;
        }

        public override bool Equals(object? obj)
        {
            return obj is SubnetKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(high, low);
        }
    }

    private sealed class CounterEntry
    {
        public long WindowSecond;
        public int Count;
    }
}
