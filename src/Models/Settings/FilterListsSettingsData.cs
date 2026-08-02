using System.Collections.Generic;

namespace OnyxFilter.Models.Settings;

// Reflète les champs de la page "Listes de blocage DNS" (/filters/blocklists).
public sealed class FilterListsSettingsData
{
    // Listes par défaut à la première installation, alignées sur celles proposées par défaut par
    // AdGuard Home : seule la première est activée. "AdAway Default Blocklist" (désactivée par défaut)
    // et "MalwareDomainList.com Hosts List" (désactivée par défaut, plus maintenue depuis 2020, conservée
    // pour compatibilité) restent présentes mais non téléchargées tant qu'elles ne sont pas activées.
    public List<FilterListEntry> Lists { get; set; } = new List<FilterListEntry>
    {
        new FilterListEntry
        {
            Id = "default-adguard-dns-filter",
            Name = "AdGuard DNS filter",
            Url = "https://adguardteam.github.io/HostlistsRegistry/assets/filter_1.txt",
            Enabled = true,
        },
        new FilterListEntry
        {
            Id = "default-adaway",
            Name = "AdAway Default Blocklist",
            Url = "https://adaway.org/hosts.txt",
            Enabled = false,
        },
        new FilterListEntry
        {
            Id = "default-malwaredomainlist",
            Name = "MalwareDomainList.com Hosts List",
            Url = "https://www.malwaredomainlist.com/hostslist/hosts.txt",
            Enabled = false,
        },
    };
}
