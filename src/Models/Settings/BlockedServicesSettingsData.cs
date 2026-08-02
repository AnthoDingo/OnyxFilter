using System.Collections.Generic;

namespace OnyxFilter.Models.Settings;

// Reflète les champs de la page "Services bloqués" (/filters/blocked-services).
public sealed class BlockedServicesSettingsData
{
    // Identifiants (BlockedServiceDefinition.Id) des services actuellement bloqués. Vide par défaut :
    // comme chez AdGuard Home, aucun service n'est bloqué tant que l'utilisateur ne le configure pas.
    public List<string> BlockedServiceIds { get; set; } = new List<string>();

    // Null : aucune suspension configurée (le blocage des services activés s'applique en permanence).
    public BlockedServicesSchedule? Schedule { get; set; }
}
