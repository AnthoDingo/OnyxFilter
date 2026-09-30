using System;
using System.Collections.Generic;

namespace OnyxFilter.Models.Settings;

// Un client persistant (page "Paramètres du client", /settings/client) : un ensemble d'appareils
// identifiés par adresse IP ou plage CIDR, avec un nom et, éventuellement, des réglages qui leur sont
// propres plutôt que les réglages globaux de l'application — comme les "clients persistants" d'AdGuard
// Home. Reste volontairement simple par rapport à AdGuard Home : pas d'identification par adresse MAC ni
// par identifiant de client ClientID (DNS-over-TLS/HTTPS), seulement IP/CIDR.
public sealed class PersistentClientEntry
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    // Adresses IP ou plages CIDR (ex. "192.168.1.42", "192.168.1.0/24") identifiant les appareils de ce
    // client.
    public IReadOnlyList<string> Identifiers { get; set; } = Array.Empty<string>();

    // Étiquettes libres ("Mots clés"), affichage seulement (aucune règle n'en dépend pour l'instant).
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    // Si vrai (par défaut), ce client suit les réglages globaux (Sécurité de navigation, Contrôle
    // parental, Recherche Sécurisée, filtrage par listes de blocage) : les champs individuels ci-dessous
    // sont alors ignorés.
    public bool UseGlobalSettings { get; set; } = true;

    public bool FilteringEnabled { get; set; } = true;

    public bool BrowsingSecurityEnabled { get; set; }

    public bool ParentalControlEnabled { get; set; }

    public bool SafeSearchEnabled { get; set; }

    // Si vrai (par défaut), ce client suit la liste globale de "Services bloqués" plutôt que
    // BlockedServiceIds ci-dessous.
    public bool UseGlobalBlockedServices { get; set; } = true;

    public IReadOnlyList<string> BlockedServiceIds { get; set; } = Array.Empty<string>();

    // Serveurs DNS en amont propres à ce client ("En amont"). Vide = suit les serveurs en amont globaux
    // de /settings/dns.
    public IReadOnlyList<string> UpstreamServers { get; set; } = Array.Empty<string>();

    public bool IgnoreQueryLog { get; set; }

    public bool IgnoreStatistics { get; set; }
}
