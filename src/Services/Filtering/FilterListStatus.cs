using System;

namespace OnyxFilter.Services.Filtering;

// État d'un abonnement à une liste de domaines (blocage ou autorisation) après la dernière tentative de
// mise à jour, pour affichage sur "Listes de blocage DNS" (/filters/blocklists) et "Listes
// d'autorisation DNS" (/filters/allowlists).
public sealed class FilterListStatus
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    // Nombre d'entrées de domaine reconnues dans cette liste lors du dernier téléchargement réussi.
    public int DomainCount { get; set; }

    public DateTime? LastUpdatedUtc { get; set; }

    // Message d'erreur du dernier échec de téléchargement/analyse, ou null si la dernière tentative a
    // réussi (ou si la liste est désactivée).
    public string? LastError { get; set; }
}
