using System.Collections.Generic;

namespace OnyxFilter.Services.BlockedServices;

// Catalogue statique des services proposés sur "Services bloqués" (/filters/blocked-services), chargé
// une fois depuis la ressource incorporée blocked-services.json (voir BlockedServicesCatalog). Ne dépend
// d'aucun réglage utilisateur : seule la liste des identifiants effectivement bloqués
// (BlockedServicesSettingsData.BlockedServiceIds) varie.
public interface IBlockedServicesCatalog
{
    IReadOnlyList<BlockedServiceDefinition> All { get; }

    IReadOnlyDictionary<string, BlockedServiceDefinition> ById { get; }
}
