using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace OnyxFilter.Services.BlockedServices;

// Implémente IBlockedServicesCatalog : charge une fois, au premier accès (singleton), le catalogue
// incorporé à la compilation (Data/BlockedServices/blocked-services.json, repris de
// AdguardTeam/HostlistsRegistry). Aucun accès disque à l'exécution : rester léger et fonctionner sans
// dépendre du répertoire de travail du processus.
public sealed class BlockedServicesCatalog : IBlockedServicesCatalog
{
    // Libellés affichés sur /filters/blocked-services pour chaque catégorie ("group") du catalogue
    // source, qui ne fournit qu'un identifiant technique (ex. "social_network") sans libellé associé.
    // L'ordre de cette liste est aussi l'ordre d'affichage des catégories sur la page.
    private static readonly (string Id, string Label)[] GroupOrder =
    {
        ("ai", "Intelligence artificielle"),
        ("cdn", "Réseaux de distribution de contenu (CDN)"),
        ("dating", "Services de rencontres"),
        ("gambling", "Jeux de hasard et paris sportifs"),
        ("gaming", "Jeux vidéo"),
        ("hosting", "Hébergement Web"),
        ("messenger", "Services de messagerie"),
        ("privacy", "Outils de confidentialité"),
        ("shopping", "Shopping"),
        ("social_network", "Réseaux sociaux"),
        ("software", "Développement de logiciels"),
        ("streaming", "Services de streaming"),
    };

    public BlockedServicesCatalog()
    {
        List<BlockedServiceDefinition> loaded = LoadFromEmbeddedResource();
        All = loaded;
        ById = loaded.ToDictionary(service => service.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<BlockedServiceDefinition> All { get; }

    public IReadOnlyDictionary<string, BlockedServiceDefinition> ById { get; }

    public static IReadOnlyList<(string Id, string Label)> Groups => GroupOrder;

    public static string GetGroupLabel(string groupId)
    {
        foreach ((string Id, string Label) group in GroupOrder)
        {
            if (string.Equals(group.Id, groupId, StringComparison.Ordinal))
            {
                return group.Label;
            }
        }

        return groupId;
    }

    // Recherche par suffixe plutôt que par nom de ressource exact reconstitué à la main : évite toute
    // dépendance fragile à la valeur exacte de RootNamespace/AssemblyName lors de la génération du nom de
    // ressource incorporée par le SDK .NET.
    private static List<BlockedServiceDefinition> LoadFromEmbeddedResource()
    {
        const string expectedSuffix = "blocked-services.json";

        Assembly assembly = typeof(BlockedServicesCatalog).Assembly;
        string? resourceName = Array.Find(assembly.GetManifestResourceNames(), name => name.EndsWith(expectedSuffix, StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new InvalidOperationException(
                "Catalogue des services bloqués introuvable (ressource incorporée manquante se terminant par « " + expectedSuffix + " »).");
        }

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
        {
            throw new InvalidOperationException("Impossible d'ouvrir la ressource incorporée « " + resourceName + " ».");
        }

        JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        List<BlockedServiceDefinition>? services = JsonSerializer.Deserialize<List<BlockedServiceDefinition>>(stream, options);

        return services ?? new List<BlockedServiceDefinition>();
    }
}
