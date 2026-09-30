using System.Collections.Generic;
using Microsoft.AspNetCore.Components;

namespace OnyxFilter.Components.Shared;

// Jeu d'icônes au trait (grille 24×24, couleur héritée via currentColor) propre à l'interface
// d'OnyxFilter. Les tracés sont des constantes : aucune donnée utilisateur n'est injectée dans le
// MarkupString rendu par Icon.razor.
public partial class Icon : ComponentBase
{
    private static readonly Dictionary<string, string> PathsByName = new Dictionary<string, string>
    {
        ["overview"] = "<rect x='3.5' y='3.5' width='7' height='8' rx='1.5'/><rect x='13.5' y='3.5' width='7' height='5' rx='1.5'/><rect x='13.5' y='11.5' width='7' height='9' rx='1.5'/><rect x='3.5' y='14.5' width='7' height='6' rx='1.5'/>",
        ["log"] = "<path d='M9 6h11M9 12h11M9 18h11'/><circle cx='4.5' cy='6' r='1'/><circle cx='4.5' cy='12' r='1'/><circle cx='4.5' cy='18' r='1'/>",
        ["blocklist"] = "<circle cx='12' cy='12' r='8.5'/><path d='m6 6 12 12'/>",
        ["allowlist"] = "<circle cx='12' cy='12' r='8.5'/><path d='m8.5 12.3 2.4 2.4 4.6-5'/>",
        ["services"] = "<path d='m12 3.5 8.5 4.5-8.5 4.5L3.5 8z'/><path d='m3.5 12.5 8.5 4.5 8.5-4.5'/><path d='m3.5 16.5 8.5 4.5 8.5-4.5' opacity='.55'/>",
        ["rules"] = "<path d='m9 7-5 5 5 5M15 7l5 5-5 5'/>",
        ["rewrites"] = "<path d='M4 8h14l-3.5-3.5M20 16H6l3.5 3.5'/>",
        ["search"] = "<circle cx='11' cy='11' r='6.5'/><path d='m20 20-4.4-4.4'/>",
        ["sliders"] = "<path d='M4 7h9M17 7h3M4 17h3M11 17h9'/><circle cx='15' cy='7' r='2'/><circle cx='9' cy='17' r='2'/>",
        ["server"] = "<rect x='3.5' y='4' width='17' height='7' rx='2'/><rect x='3.5' y='13' width='17' height='7' rx='2'/><path d='M7.5 7.5h.01M7.5 16.5h.01'/>",
        ["lock"] = "<rect x='4.5' y='10.5' width='15' height='10' rx='2'/><path d='M8 10.5V8a4 4 0 0 1 8 0v2.5'/>",
        ["devices"] = "<rect x='2.5' y='4.5' width='14' height='10' rx='1.5'/><path d='M6.5 19h6M9.5 14.5V19'/><rect x='17.5' y='8.5' width='4' height='11' rx='1'/>",
        ["book"] = "<path d='M5 4.5A1.5 1.5 0 0 1 6.5 3H19v15H6.5A1.5 1.5 0 0 0 5 19.5z'/><path d='M5 19.5A1.5 1.5 0 0 0 6.5 21H19'/>",
        ["user"] = "<circle cx='12' cy='8.5' r='3.5'/><path d='M5 20a7 7 0 0 1 14 0'/>",
        ["logout"] = "<path d='M14 4h4a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-4'/><path d='m10 16-4-4 4-4M6 12h10'/>",
        ["refresh"] = "<path d='M20 11a8 8 0 0 0-14.6-4.5L4 8'/><path d='M4 4v4h4'/><path d='M4 13a8 8 0 0 0 14.6 4.5L20 16'/><path d='M20 20v-4h-4'/>",
        ["shield"] = "<path d='M12 3 4.5 6v5.5c0 4.6 3.2 8.4 7.5 9.5 4.3-1.1 7.5-4.9 7.5-9.5V6z'/><path d='m9 12 2.2 2.2L15.5 10'/>",
        ["shield-off"] = "<path d='M12 3 4.5 6v5.5c0 4.6 3.2 8.4 7.5 9.5 4.3-1.1 7.5-4.9 7.5-9.5V6z'/><path d='M12 8v4.5M12 15.5h.01'/>",
        ["pause"] = "<path d='M9 5v14M15 5v14'/>",
        ["play"] = "<path d='m7 4.5 12 7.5-12 7.5z'/>",
        ["edit"] = "<path d='M14.5 5.5 18.5 9.5'/><path d='M4 20l1-4.5L15.5 5a2.1 2.1 0 0 1 3 3L8 18.5z'/>",
        ["trash"] = "<path d='M4.5 7h15M10 11v6M14 11v6'/><path d='M6 7l1 12.5A1.5 1.5 0 0 0 8.5 21h7a1.5 1.5 0 0 0 1.5-1.5L18 7'/><path d='M9 7V4.5A1.5 1.5 0 0 1 10.5 3h3A1.5 1.5 0 0 1 15 4.5V7'/>",
        ["plus"] = "<path d='M12 5v14M5 12h14'/>",
        ["download"] = "<path d='M12 4v11M7.5 10.5 12 15l4.5-4.5'/><path d='M5 19.5h14'/>",
        ["clock"] = "<circle cx='12' cy='12' r='8.5'/><path d='M12 7.5V12l3 2'/>",
        ["bolt"] = "<path d='M13 3 5 13.5h6L10.5 21 19 10.5h-6z'/>",
        ["globe"] = "<circle cx='12' cy='12' r='8.5'/><path d='M3.5 12h17M12 3.5c2.4 2.3 3.5 5.2 3.5 8.5s-1.1 6.2-3.5 8.5c-2.4-2.3-3.5-5.2-3.5-8.5S9.6 5.8 12 3.5z'/>",
        ["sun"] = "<circle cx='12' cy='12' r='3.5'/><path d='M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4'/>",
        ["moon"] = "<path d='M19.5 14.5A8 8 0 0 1 9.5 4.5a8 8 0 1 0 10 10z'/>",
        ["contrast"] = "<circle cx='12' cy='12' r='8.5'/><path d='M12 3.5v17a8.5 8.5 0 0 0 0-17z' fill='currentColor' stroke='none'/>",
        ["menu"] = "<path d='M4 7h16M4 12h16M4 17h10'/>",
        ["close"] = "<path d='M6 6l12 12M18 6 6 18'/>",
        ["external"] = "<path d='M14 4h6v6M20 4l-9 9'/><path d='M18 14v4.5A1.5 1.5 0 0 1 16.5 20h-11A1.5 1.5 0 0 1 4 18.5v-11A1.5 1.5 0 0 1 5.5 6H10'/>",
        ["key"] = "<circle cx='8' cy='15' r='4.5'/><path d='m11.5 11.5 8-8M16 7l2.5 2.5'/>",
        ["alert"] = "<path d='M12 4 2.8 19.5h18.4z'/><path d='M12 10v4.5M12 17.2h.01'/>",
        ["plug"] = "<path d='M9 3v5M15 3v5'/><path d='M6.5 8h11v3.5a5.5 5.5 0 0 1-11 0z'/><path d='M12 17v4'/>",
        ["compass"] = "<circle cx='12' cy='12' r='8.5'/><path d='m15.5 8.5-2 5-5 2 2-5z'/>",
    };

    // Nom de l'icône (clé de PathsByName ci-dessus). Un nom inconnu rend une icône vide.
    [Parameter]
    public string Name { get; set; } = string.Empty;

    [Parameter]
    public string? Class { get; set; }

    // Chaîne plutôt que double : rendue telle quelle dans l'attribut SVG, sans dépendre de la culture
    // courante (« 1,75 » en fr-FR serait invalide).
    [Parameter]
    public string StrokeWidth { get; set; } = "1.75";

    private string Paths => PathsByName.TryGetValue(Name, out string? paths) ? paths : string.Empty;
}
