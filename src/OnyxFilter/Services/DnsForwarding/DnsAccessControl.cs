using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.DnsForwarding;

// Applique les paramètres d'accès (AllowedClients, DisallowedClients, DisallowedDomains) de la page
// "Paramètres DNS". Conçu pour rester léger : les règles clients sont précompilées en préfixes de
// 128 bits (aucune allocation par requête), les domaines interdits sont stockés dans un HashSet.
public sealed class DnsAccessControl : IDnsAccessControl, IDisposable
{
    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<DnsAccessControl> logger;
    private readonly object syncRoot = new object();

    private ClientRule[] allowedClients = Array.Empty<ClientRule>();
    private ClientRule[] disallowedClients = Array.Empty<ClientRule>();

    // Domaines interdits exacts (ex. "example.org") et suffixes interdits (ex. "*.example.org",
    // stocké sans le préfixe "*."), comparés en minuscules.
    private HashSet<string> disallowedDomains = new HashSet<string>(StringComparer.Ordinal);
    private string[] disallowedDomainSuffixes = Array.Empty<string>();

    public DnsAccessControl(ILocalSettingsStore settingsStore, ILogger<DnsAccessControl> logger)
    {
        this.settingsStore = settingsStore;
        this.logger = logger;
        this.settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync()
    {
        await ReloadAsync();
    }

    public bool IsClientAllowed(IPAddress clientAddress)
    {
        ClientRule[] allowed;
        ClientRule[] disallowed;

        lock (syncRoot)
        {
            allowed = allowedClients;
            disallowed = disallowedClients;
        }

        AddressBits address = AddressBits.FromIpAddress(clientAddress);

        // Liste blanche non vide : seuls les clients listés sont servis, la liste noire est ignorée.
        if (allowed.Length > 0)
        {
            return MatchesAny(allowed, address);
        }

        return !MatchesAny(disallowed, address);
    }

    public bool IsDomainDisallowed(string domain)
    {
        HashSet<string> exactDomains;
        string[] suffixes;

        lock (syncRoot)
        {
            exactDomains = disallowedDomains;
            suffixes = disallowedDomainSuffixes;
        }

        if (exactDomains.Count == 0 && suffixes.Length == 0)
        {
            return false;
        }

        string normalized = domain.TrimEnd('.').ToLowerInvariant();

        if (exactDomains.Contains(normalized))
        {
            return true;
        }

        foreach (string suffix in suffixes)
        {
            // "*.example.org" interdit "example.org" et tous ses sous-domaines.
            if (normalized.Length == suffix.Length)
            {
                if (string.Equals(normalized, suffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (normalized.Length > suffix.Length
                && normalized.EndsWith(suffix, StringComparison.Ordinal)
                && normalized[normalized.Length - suffix.Length - 1] == '.')
            {
                return true;
            }
        }

        return false;
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

            ClientRule[] allowed = ParseClientRules(settings.Dns.AllowedClients, "clients autorisés");
            ClientRule[] disallowed = ParseClientRules(settings.Dns.DisallowedClients, "clients interdits");
            HashSet<string> exactDomains = new HashSet<string>(StringComparer.Ordinal);
            List<string> suffixes = new List<string>();

            foreach (string entry in settings.Dns.DisallowedDomains)
            {
                string domain = entry.Trim().TrimEnd('.').ToLowerInvariant();

                if (domain.Length == 0 || domain.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (domain.StartsWith("*.", StringComparison.Ordinal))
                {
                    string suffix = domain.Substring(2);

                    if (suffix.Length > 0)
                    {
                        suffixes.Add(suffix);
                    }
                }
                else
                {
                    exactDomains.Add(domain);
                }
            }

            lock (syncRoot)
            {
                allowedClients = allowed;
                disallowedClients = disallowed;
                disallowedDomains = exactDomains;
                disallowedDomainSuffixes = suffixes.ToArray();
            }

            logger.LogInformation(
                "Paramètres d'accès DNS : {AllowedCount} client(s) autorisé(s), {DisallowedCount} client(s) interdit(s), {DomainCount} règle(s) de domaines interdits.",
                allowed.Length,
                disallowed.Length,
                exactDomains.Count + suffixes.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de charger les paramètres d'accès DNS depuis appsettings.local.json.");
        }
    }

    // Analyse les entrées "adresse IP" ou "sous-réseau CIDR". Les entrées invalides sont ignorées avec
    // un avertissement (elles ne doivent ni bloquer le service, ni ouvrir l'accès par accident).
    private ClientRule[] ParseClientRules(IReadOnlyList<string> entries, string listName)
    {
        List<ClientRule> rules = new List<ClientRule>(entries.Count);

        foreach (string entry in entries)
        {
            string trimmed = entry.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryParseClientRule(trimmed, out ClientRule rule))
            {
                rules.Add(rule);
            }
            else
            {
                logger.LogWarning("Entrée ignorée dans la liste des {ListName} : « {Entry} » (adresse IP ou CIDR attendu).", listName, trimmed);
            }
        }

        return rules.ToArray();
    }

    private static bool TryParseClientRule(string entry, out ClientRule rule)
    {
        rule = default;

        string addressPart = entry;
        int prefixLength = -1;
        int slashIndex = entry.IndexOf('/');

        if (slashIndex >= 0)
        {
            addressPart = entry.Substring(0, slashIndex);

            if (!int.TryParse(entry.AsSpan(slashIndex + 1), out prefixLength) || prefixLength < 0)
            {
                return false;
            }
        }

        if (!IPAddress.TryParse(addressPart, out IPAddress? address))
        {
            return false;
        }

        bool isIpv4 = address.AddressFamily == AddressFamily.InterNetwork || address.IsIPv4MappedToIPv6;
        int maxPrefixLength = isIpv4 ? 32 : 128;

        if (prefixLength < 0)
        {
            prefixLength = maxPrefixLength;
        }
        else if (prefixLength > maxPrefixLength)
        {
            return false;
        }

        // Les adresses IPv4 sont stockées au format IPv6 mappé ("::ffff:a.b.c.d"), pour comparer
        // uniformément avec les clients reçus via une socket dual-stack.
        int totalPrefixLength = isIpv4 ? 96 + prefixLength : prefixLength;
        AddressBits bits = AddressBits.FromIpAddress(address);
        rule = new ClientRule(bits.Mask(totalPrefixLength), totalPrefixLength);
        return true;
    }

    private static bool MatchesAny(ClientRule[] rules, AddressBits address)
    {
        foreach (ClientRule rule in rules)
        {
            if (rule.Matches(address))
            {
                return true;
            }
        }

        return false;
    }

    // Adresse IP sous forme de 128 bits (IPv4 mappé en "::ffff:a.b.c.d"), sans allocation sur le tas.
    private readonly struct AddressBits
    {
        public readonly ulong High;
        public readonly ulong Low;

        public AddressBits(ulong high, ulong low)
        {
            High = high;
            Low = low;
        }

        public static AddressBits FromIpAddress(IPAddress address)
        {
            Span<byte> bytes = stackalloc byte[16];

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                bytes.Clear();
                bytes[10] = 0xFF;
                bytes[11] = 0xFF;
                address.TryWriteBytes(bytes.Slice(12), out int _);
            }
            else
            {
                address.TryWriteBytes(bytes, out int _);
            }

            return new AddressBits(ReadUInt64BigEndian(bytes, 0), ReadUInt64BigEndian(bytes, 8));
        }

        public AddressBits Mask(int prefixLength)
        {
            ulong high = High;
            ulong low = Low;

            if (prefixLength <= 0)
            {
                high = 0;
                low = 0;
            }
            else if (prefixLength < 64)
            {
                high &= ulong.MaxValue << (64 - prefixLength);
                low = 0;
            }
            else if (prefixLength == 64)
            {
                // Cas à part : "ulong.MaxValue << 64" ne vaut pas 0 en C# (le décalage est pris modulo 64).
                low = 0;
            }
            else if (prefixLength < 128)
            {
                low &= ulong.MaxValue << (128 - prefixLength);
            }

            return new AddressBits(high, low);
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
    }

    private readonly struct ClientRule
    {
        private readonly AddressBits network;
        private readonly int prefixLength;

        public ClientRule(AddressBits network, int prefixLength)
        {
            this.network = network;
            this.prefixLength = prefixLength;
        }

        public bool Matches(AddressBits address)
        {
            AddressBits masked = address.Mask(prefixLength);
            return masked.High == network.High && masked.Low == network.Low;
        }
    }
}
