using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.DnsForwarding;

// Applique les paramètres d'accès (AllowedClients, DisallowedClients, AlwaysAllowedClients, DisallowedDomains)
// de la page
// "Paramètres DNS". Conçu pour rester léger : les règles clients sont précompilées en préfixes de
// 128 bits (ClientAccessRule, aucune allocation par requête), les domaines interdits sont stockés dans un
// HashSet.
public sealed class DnsAccessControl : IDnsAccessControl, IDisposable
{
    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<DnsAccessControl> logger;
    private readonly object syncRoot = new object();

    private ClientAccessRule[] allowedClients = Array.Empty<ClientAccessRule>();
    private ClientAccessRule[] disallowedClients = Array.Empty<ClientAccessRule>();
    private ClientAccessRule[] alwaysAllowedClients = Array.Empty<ClientAccessRule>();

    // Numéro de chaque rechargement, pour qu'un rechargement lent, commencé avant une modification, n'écrase
    // pas les règles appliquées par un rechargement plus récent.
    private long reloadGeneration;
    private long appliedGeneration;
    private volatile bool isLoaded;

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
        if (!isLoaded)
        {
            await ReloadAsync();
        }
    }

    public bool IsClientAllowed(IPAddress clientAddress)
    {
        return Evaluate(clientAddress).Allowed;
    }

    public ClientAccessDecision Evaluate(IPAddress clientAddress)
    {
        ClientAccessRule[] allowed;
        ClientAccessRule[] disallowed;
        ClientAccessRule[] alwaysAllowed;

        lock (syncRoot)
        {
            allowed = allowedClients;
            disallowed = disallowedClients;
            alwaysAllowed = alwaysAllowedClients;
        }

        AddressBits address = AddressBits.FromIpAddress(clientAddress);
        ClientAccessMode mode = allowed.Length > 0 ? ClientAccessMode.Allowlist : ClientAccessMode.Blocklist;

        // Les clients toujours autorisés l'emportent sur toute autre règle.
        string? exceptionRule = FindMatch(alwaysAllowed, address);

        if (exceptionRule is not null)
        {
            return new ClientAccessDecision(true, mode, exceptionRule, ClientAccessList.AlwaysAllowed);
        }

        // Liste blanche non vide : seuls les clients listés sont servis, la liste noire est ignorée.
        if (mode == ClientAccessMode.Allowlist)
        {
            string? allowingRule = FindMatch(allowed, address);
            return allowingRule is null
                ? new ClientAccessDecision(false, mode, null, null)
                : new ClientAccessDecision(true, mode, allowingRule, ClientAccessList.Allowed);
        }

        string? blockingRule = FindMatch(disallowed, address);
        return blockingRule is null
            ? new ClientAccessDecision(true, mode, null, null)
            : new ClientAccessDecision(false, mode, blockingRule, ClientAccessList.Blocked);
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

    public async Task ReloadAsync()
    {
        long generation = Interlocked.Increment(ref reloadGeneration);

        try
        {
            AppLocalSettings settings = await settingsStore.LoadAsync();

            ClientAccessRule[] allowed = ParseClientRules(settings.Dns.AllowedClients, "clients autorisés");
            ClientAccessRule[] disallowed = ParseClientRules(settings.Dns.DisallowedClients, "clients interdits");
            ClientAccessRule[] alwaysAllowed = ParseClientRules(settings.Dns.AlwaysAllowedClients, "clients toujours autorisés");
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
                // Chaque écriture des réglages déclenche un nouveau rechargement, qui lit le fichier après
                // elle : les règles d'un rechargement plus ancien ne doivent donc jamais le remplacer.
                if (generation < appliedGeneration)
                {
                    return;
                }

                appliedGeneration = generation;
                isLoaded = true;
                allowedClients = allowed;
                disallowedClients = disallowed;
                alwaysAllowedClients = alwaysAllowed;
                disallowedDomains = exactDomains;
                disallowedDomainSuffixes = suffixes.ToArray();
            }

            logger.LogInformation(
                "Paramètres d'accès DNS : {AllowedCount} client(s) autorisé(s), {DisallowedCount} client(s) interdit(s), {AlwaysAllowedCount} client(s) toujours autorisé(s), {DomainCount} règle(s) de domaines interdits.",
                allowed.Length,
                disallowed.Length,
                alwaysAllowed.Length,
                exactDomains.Count + suffixes.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de charger les paramètres d'accès DNS depuis appsettings.local.json.");
        }
    }

    // Analyse les entrées "adresse IP" ou "sous-réseau CIDR". Les entrées invalides sont ignorées avec
    // un avertissement (elles ne doivent ni bloquer le service, ni ouvrir l'accès par accident).
    private ClientAccessRule[] ParseClientRules(IReadOnlyList<string> entries, string listName)
    {
        List<ClientAccessRule> rules = new List<ClientAccessRule>(entries.Count);

        foreach (string entry in entries)
        {
            if (ClientAccessLists.IsCommentOrBlank(entry))
            {
                continue;
            }

            if (ClientAccessRule.TryParse(entry, out ClientAccessRule rule))
            {
                rules.Add(rule);
            }
            else
            {
                logger.LogWarning("Entrée ignorée dans la liste des {ListName} : « {Entry} » (adresse IP ou CIDR attendu).", listName, entry.Trim());
            }
        }

        return rules.ToArray();
    }

    private static string? FindMatch(ClientAccessRule[] rules, AddressBits address)
    {
        foreach (ClientAccessRule rule in rules)
        {
            if (rule.Matches(address))
            {
                return rule.Source;
            }
        }

        return null;
    }
}
