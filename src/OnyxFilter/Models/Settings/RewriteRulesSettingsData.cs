using System.Collections.Generic;

namespace OnyxFilter.Models.Settings;

// Reflète les champs de la page "Réécritures DNS" (/filters/rewrites).
public sealed class RewriteRulesSettingsData
{
    // Activation globale de la fonctionnalité, indépendante de l'activation individuelle de chaque règle
    // (case "Activé" de la page) : permet de désactiver temporairement toutes les réécritures sans perdre
    // la liste configurée.
    public bool Enabled { get; set; } = true;

    public List<DnsRewriteEntry> Entries { get; set; } = new List<DnsRewriteEntry>();
}
