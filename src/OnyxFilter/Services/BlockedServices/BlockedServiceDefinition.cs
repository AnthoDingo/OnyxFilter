using System.Collections.Generic;

namespace OnyxFilter.Services.BlockedServices;

// Une entrée du catalogue des services pour "Services bloqués" (/filters/blocked-services), reprise
// telle quelle de AdguardTeam/HostlistsRegistry (assets/services.json) : désérialisée directement depuis
// la ressource incorporée blocked-services.json (voir BlockedServicesCatalog), jamais modifiée en
// mémoire. "Group" est l'identifiant de catégorie source (ex. "social_network"), sans libellé affichable
// associé dans le catalogue d'origine — voir BlockedServicesCatalog.GetGroupLabel.
public sealed class BlockedServiceDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;

    // Icône au format SVG ("currentColor"), à afficher telle quelle (ressource de confiance, incorporée
    // à la compilation, jamais issue d'une saisie utilisateur).
    public string Icon { get; set; } = string.Empty;

    // Règles de blocage au format des listes de blocage/règles personnalisées (voir
    // BlockedServicesService pour le détail de leur interprétation).
    public List<string> Rules { get; set; } = new List<string>();
}
