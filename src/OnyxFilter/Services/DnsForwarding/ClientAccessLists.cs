using System;
using System.Collections.Generic;
using System.Linq;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.DnsForwarding;

// Mode d'accès au DNS, déduit de la liste des clients autorisés : dès qu'elle contient une règle valide,
// seuls ces clients (et les clients toujours autorisés) sont servis, et la liste des clients refusés est
// ignorée.
public enum ClientAccessMode
{
    Blocklist,
    Allowlist,
}

// Les trois listes d'accès des paramètres DNS.
public enum ClientAccessList
{
    // AllowedClients : liste blanche, exclusive dès qu'elle n'est pas vide.
    Allowed,

    // DisallowedClients : clients refusés (mode liste noire).
    Blocked,

    // AlwaysAllowedClients : servis dans tous les cas, prioritaires sur les clients refusés. Permet
    // d'autoriser une adresse précise dans un sous-réseau bloqué sans débloquer le reste.
    AlwaysAllowed,
}

// Décision pour une adresse : servie ou non, et la règle en cause avec sa liste (null si aucune règle ne la
// concerne).
public readonly record struct ClientAccessDecision(bool Allowed, ClientAccessMode Mode, string? Rule, ClientAccessList? RuleList);

public sealed record ClientAccessListEntry(ClientAccessList List, string Rule);

public enum ClientAccessOutcome
{
    // Les listes ont été modifiées.
    Changed,

    // Rien à faire : le client était déjà dans l'état demandé.
    Unchanged,

    // Une règle plus large (ex. un sous-réseau) s'y oppose ; elle est citée dans Conflicts.
    Conflict,

    // Blocage refusé : il viderait la liste des clients autorisés et ouvrirait le DNS à tous les clients.
    AllowlistWouldBeEmpty,
}

public sealed record ClientAccessChange(
    ClientAccessOutcome Outcome,
    ClientAccessMode Mode,
    IReadOnlyList<ClientAccessListEntry> Added,
    IReadOnlyList<ClientAccessListEntry> Removed,
    IReadOnlyList<ClientAccessListEntry> Conflicts);

// Autorise ou bloque un client (adresse IP ou sous-réseau) en modifiant les listes d'accès des paramètres
// DNS, sans jamais changer de mode. Une règle plus précise l'emporte sur une règle plus large :
//   - autoriser retire les règles refusées que le client couvre entièrement ; s'il reste couvert par une
//     règle refusée plus large (ex. 203.0.113.5 dans 203.0.113.0/24), il est ajouté aux clients toujours
//     autorisés : il est débloqué, le reste du sous-réseau ne l'est pas. En mode liste blanche, il est
//     simplement ajouté aux clients autorisés ;
//   - bloquer retire les règles toujours autorisées (et, en mode liste blanche, autorisées) que le client
//     couvre entièrement, puis l'ajoute aux clients refusés en mode liste noire.
// Seule limite : une règle autorisée plus large que le client bloqué n'est jamais modifiée d'office
// (l'opération est refusée et la règle citée), puisque les clients refusés ne peuvent pas y faire
// d'exception. Les commentaires et les entrées invalides des listes sont conservés tels quels.
public static class ClientAccessLists
{
    public static ClientAccessMode GetMode(DnsSettingsData dns)
    {
        return ValidRules(dns.AllowedClients).Any() ? ClientAccessMode.Allowlist : ClientAccessMode.Blocklist;
    }

    public static IReadOnlyList<string> GetEntries(DnsSettingsData dns, ClientAccessList list)
    {
        return list switch
        {
            ClientAccessList.Allowed => dns.AllowedClients,
            ClientAccessList.Blocked => dns.DisallowedClients,
            _ => dns.AlwaysAllowedClients,
        };
    }

    // Règles valides d'une liste, dans l'ordre (commentaires, lignes vides et entrées invalides exclus).
    public static IEnumerable<ClientAccessRule> ValidRules(IEnumerable<string> entries)
    {
        foreach (string entry in entries)
        {
            if (IsCommentOrBlank(entry))
            {
                continue;
            }

            if (ClientAccessRule.TryParse(entry, out ClientAccessRule rule))
            {
                yield return rule;
            }
        }
    }

    public static bool IsCommentOrBlank(string entry)
    {
        string trimmed = entry.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal);
    }

    public static ClientAccessChange Allow(DnsSettingsData dns, ClientAccessRule client)
    {
        ClientAccessMode mode = GetMode(dns);
        EditSession session = new EditSession(dns, mode);

        if (session.IsCovered(ClientAccessList.AlwaysAllowed, client))
        {
            return session.Result();
        }

        if (mode == ClientAccessMode.Allowlist)
        {
            if (!session.IsCovered(ClientAccessList.Allowed, client))
            {
                session.Add(ClientAccessList.Allowed, client);
            }

            return session.Result();
        }

        // Les règles refusées plus larges que le client restent en place : l'exception passe par les clients
        // toujours autorisés.
        IReadOnlyList<ClientAccessListEntry> broaderBlocks = session.RemoveContained(ClientAccessList.Blocked, client);

        if (broaderBlocks.Count > 0)
        {
            session.Add(ClientAccessList.AlwaysAllowed, client);
        }

        return session.Result();
    }

    public static ClientAccessChange Block(DnsSettingsData dns, ClientAccessRule client)
    {
        ClientAccessMode mode = GetMode(dns);
        EditSession session = new EditSession(dns, mode);

        List<ClientAccessListEntry> conflicts = new List<ClientAccessListEntry>();
        conflicts.AddRange(session.RemoveContained(ClientAccessList.AlwaysAllowed, client));

        if (mode == ClientAccessMode.Allowlist)
        {
            conflicts.AddRange(session.RemoveContained(ClientAccessList.Allowed, client));

            if (conflicts.Count == 0 && session.HasRemovedFrom(ClientAccessList.Allowed) && !ValidRules(session.Entries(ClientAccessList.Allowed)).Any())
            {
                return new ClientAccessChange(ClientAccessOutcome.AllowlistWouldBeEmpty, mode, Array.Empty<ClientAccessListEntry>(), session.Removed, Array.Empty<ClientAccessListEntry>());
            }
        }
        else if (!session.IsCovered(ClientAccessList.Blocked, client))
        {
            session.Add(ClientAccessList.Blocked, client);
        }

        if (conflicts.Count > 0)
        {
            return new ClientAccessChange(ClientAccessOutcome.Conflict, mode, Array.Empty<ClientAccessListEntry>(), Array.Empty<ClientAccessListEntry>(), conflicts);
        }

        return session.Result();
    }

    // Modifications en cours sur des copies des listes ; appliquées aux paramètres seulement par Result(),
    // et seulement si quelque chose a changé.
    private sealed class EditSession
    {
        private readonly DnsSettingsData dns;
        private readonly ClientAccessMode mode;
        private readonly Dictionary<ClientAccessList, List<string>> lists = new Dictionary<ClientAccessList, List<string>>();
        private readonly List<ClientAccessListEntry> added = new List<ClientAccessListEntry>();
        private readonly List<ClientAccessListEntry> removed = new List<ClientAccessListEntry>();

        public EditSession(DnsSettingsData dns, ClientAccessMode mode)
        {
            this.dns = dns;
            this.mode = mode;

            foreach (ClientAccessList list in Enum.GetValues<ClientAccessList>())
            {
                lists[list] = GetEntries(dns, list).ToList();
            }
        }

        public IReadOnlyList<ClientAccessListEntry> Removed => removed;

        public IReadOnlyList<string> Entries(ClientAccessList list)
        {
            return lists[list];
        }

        public bool IsCovered(ClientAccessList list, ClientAccessRule client)
        {
            return ValidRules(lists[list]).Any(rule => rule.Contains(client));
        }

        public bool HasRemovedFrom(ClientAccessList list)
        {
            return removed.Any(entry => entry.List == list);
        }

        public void Add(ClientAccessList list, ClientAccessRule client)
        {
            string rule = client.ToString();
            lists[list].Add(rule);
            added.Add(new ClientAccessListEntry(list, rule));
        }

        // Retire de la liste les règles que le client couvre entièrement, et renvoie celles qui le couvrent
        // sans qu'il les couvre (règles plus larges, laissées en place). Deux préfixes qui se chevauchent
        // sont toujours dans l'un de ces deux cas.
        public IReadOnlyList<ClientAccessListEntry> RemoveContained(ClientAccessList list, ClientAccessRule client)
        {
            List<string> kept = new List<string>(lists[list].Count);
            List<ClientAccessListEntry> broader = new List<ClientAccessListEntry>();

            foreach (string entry in lists[list])
            {
                if (IsCommentOrBlank(entry) || !ClientAccessRule.TryParse(entry, out ClientAccessRule rule))
                {
                    kept.Add(entry);
                    continue;
                }

                if (client.Contains(rule))
                {
                    removed.Add(new ClientAccessListEntry(list, rule.Source));
                    continue;
                }

                if (rule.Overlaps(client))
                {
                    broader.Add(new ClientAccessListEntry(list, rule.Source));
                }

                kept.Add(entry);
            }

            lists[list] = kept;
            return broader;
        }

        public ClientAccessChange Result()
        {
            if (added.Count == 0 && removed.Count == 0)
            {
                return new ClientAccessChange(ClientAccessOutcome.Unchanged, mode, Array.Empty<ClientAccessListEntry>(), Array.Empty<ClientAccessListEntry>(), Array.Empty<ClientAccessListEntry>());
            }

            dns.AllowedClients = lists[ClientAccessList.Allowed];
            dns.DisallowedClients = lists[ClientAccessList.Blocked];
            dns.AlwaysAllowedClients = lists[ClientAccessList.AlwaysAllowed];
            return new ClientAccessChange(ClientAccessOutcome.Changed, mode, added, removed, Array.Empty<ClientAccessListEntry>());
        }
    }
}
