using Microsoft.AspNetCore.Components;

namespace OnyxFilter.Components.Shared;

// Panneau de la vue d'ensemble (Home.razor) : titre, légende courte et contenu libre. L'actualisation
// est globale à la page (bouton « Actualiser » et mode direct), il n'y a donc pas de bouton par panneau.
public partial class DashboardPanel : ComponentBase
{
    [Parameter]
    public string Title { get; set; } = string.Empty;

    [Parameter]
    public string? Caption { get; set; }

    // Classe CSS supplémentaire (ex. placement dans la grille de la page).
    [Parameter]
    public string? Class { get; set; }

    // Contenu optionnel aligné à droite de l'en-tête (légende, pastille…).
    [Parameter]
    public RenderFragment? Aside { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }
}
