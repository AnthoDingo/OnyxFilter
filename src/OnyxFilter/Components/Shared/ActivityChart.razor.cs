using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Services.Statistics;

namespace OnyxFilter.Components.Shared;

// Histogramme horaire empilé de la vue d'ensemble (Home.razor) : une colonne par heure (série
// DnsStatisticsSnapshot.HourlySeries, la plus ancienne à gauche), part bloquée en bas, part autorisée
// au-dessus. Rendu en HTML/CSS (hauteurs en pourcentage) plutôt qu'en SVG : net à toutes les tailles,
// sans calcul de tracé, et léger pour un serveur modeste.
public partial class ActivityChart : ComponentBase
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("fr-FR");

    [Parameter]
    public IReadOnlyList<DnsStatisticsHourlyPoint> Points { get; set; } = Array.Empty<DnsStatisticsHourlyPoint>();

    private sealed class Bar
    {
        public string HeightPercent { get; init; } = "0%";

        public string BlockedPercent { get; init; } = "0%";

        public string HourLabel { get; init; } = string.Empty;

        public string Tooltip { get; init; } = string.Empty;
    }

    private List<Bar> Bars { get; } = new List<Bar>();

    // Graduation haute de l'axe, arrondie à une valeur « ronde » paire (voir NiceCeiling).
    private long ScaleMax { get; set; }

    private bool IsEmpty { get; set; } = true;

    private string AriaSummary { get; set; } = string.Empty;

    protected override void OnParametersSet()
    {
        Bars.Clear();

        long peak = Points.Count == 0 ? 0 : Points.Max(point => point.TotalQueries);
        IsEmpty = peak <= 0;
        ScaleMax = NiceCeiling(peak);

        foreach (DnsStatisticsHourlyPoint point in Points)
        {
            DateTime start = point.HourStartUtc.ToLocalTime();
            long blocked = Math.Min(point.BlockedQueries, point.TotalQueries);

            Bars.Add(new Bar
            {
                HeightPercent = ToPercent(point.TotalQueries, ScaleMax),
                BlockedPercent = ToPercent(blocked, point.TotalQueries),
                HourLabel = start.Hour.ToString(CultureInfo.InvariantCulture) + " h",
                Tooltip = string.Create(
                    DisplayCulture,
                    $"{start:HH} h – {start.AddHours(1):HH} h · {FormatCount(point.TotalQueries)} requête(s), dont {FormatCount(blocked)} bloquée(s)"),
            });
        }

        long total = Points.Sum(point => point.TotalQueries);
        long totalBlocked = Points.Sum(point => point.BlockedQueries);
        AriaSummary = string.Create(
            DisplayCulture,
            $"Requêtes par heure sur les dernières 24 heures : {FormatCount(total)} au total, dont {FormatCount(totalBlocked)} bloquées.");
    }

    private static string FormatCount(long value) => value.ToString("N0", DisplayCulture);

    // Pourcentage pour un attribut style : toujours en culture invariante (« 12.5% »), une virgule
    // décimale rendrait la déclaration CSS invalide.
    private static string ToPercent(long part, long whole)
    {
        if (whole <= 0 || part <= 0)
        {
            return "0%";
        }

        double ratio = Math.Min(1.0, part / (double)whole);
        return (ratio * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
    }

    // Arrondit le maximum au palier supérieur parmi 2, 4, 6, 8, 10 × 10^n : la graduation médiane
    // (ScaleMax / 2) reste ainsi un entier exact.
    private static long NiceCeiling(long value)
    {
        if (value <= 0)
        {
            return 0;
        }

        long magnitude = 1;
        while (magnitude * 10 <= value)
        {
            magnitude *= 10;
        }

        foreach (long step in new long[] { 2, 4, 6, 8, 10 })
        {
            if (step * magnitude >= value)
            {
                return step * magnitude;
            }
        }

        return 10 * magnitude;
    }
}
