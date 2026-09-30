using System;
using System.Collections.Generic;

namespace OnyxFilter.Services.Filtering;

// Instantané de l'état des listes de domaines, pour affichage sur /filters/blocklists (listes de
// blocage) ou /filters/allowlists (listes d'autorisation).
public sealed class FilterListsSnapshot
{
    public IReadOnlyList<FilterListStatus> Lists { get; set; } = Array.Empty<FilterListStatus>();

    // Nombre total de domaines uniques actuellement bloqués ou autorisés (selon l'usage), toutes listes
    // activées confondues.
    public int TotalDomainCount { get; set; }

    public DateTime? LastRefreshUtc { get; set; }
}
