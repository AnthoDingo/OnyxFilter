using System.Globalization;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Services.ClientLocation;

namespace OnyxFilter.Components.Shared;

// "Location" : pays et fournisseur d'accès, renseignés uniquement pour le classement des clients.
// "DisplayName" : nom du client persistant (/settings/client) correspondant, affiché à la place de "Label".
public sealed record RankItem(
    string Label,
    long Count,
    ClientLocation? Location = null,
    string? DisplayName = null
);

public enum RankTone
{
    Accent,
    Blocked,
    Neutral
}

// Classement de la vue d'ensemble (domaines, clients…) : une ligne par entrée, avec une barre de fond
// proportionnelle à la première entrée du classement.
public partial class RankList : ComponentBase
{
    private static CultureInfo DisplayCulture => CultureInfo.CurrentCulture;

    [Parameter]
    public IReadOnlyList<RankItem> Items { get; set; } = Array.Empty<RankItem>();

    [Parameter]
    public RankTone Tone { get; set; } = RankTone.Accent;

    [Parameter]
    public string? EmptyText { get; set; }

    private long MaxCount => Items.Count == 0 ? 0 : Items.Max(item => item.Count);

    private static string FormatCount(long value) => value.ToString("N0", DisplayCulture);

    private static string TooltipOf(RankItem item)
    {
        string tooltip = item.DisplayName is null
            ? item.Label
            : $"{item.DisplayName} ({item.Label})";

        return item.Location is null
            ? tooltip
            : $"{tooltip} — {item.Location.CountryCode} AS{item.Location.Asn} {item.Location.Description}";
    }

    // Valeur de la propriété CSS --share, en culture invariante (voir ActivityChart.ToPercent).
    private string ShareOf(RankItem item)
    {
        long max = MaxCount;
        if (max <= 0 || item.Count <= 0)
        {
            return "0%";
        }

        return (item.Count * 100.0 / max).ToString("0.##", CultureInfo.InvariantCulture) + "%";
    }
}
