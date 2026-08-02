using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Components;

namespace OnyxFilter.Components.Shared;

public enum StatCardAccent
{
    Neutral,
    Orange,
    Purple
}

// Tracé de repli utilisé quand aucune série de données réelle n'est fournie via le paramètre Series
// (ex. cartes "Malware" et "Contenu adulte" de Home.razor, pas encore catégorisées côté moteur DNS).
public enum SparklineShape
{
    Flat,
    Spike
}

public partial class StatCard : ComponentBase
{
    // Dimensions du viewBox SVG (voir StatCard.razor) : la série de points y est normalisée.
    private const int ChartWidth = 300;
    private const int ChartHeight = 60;
    private const int ChartTopPadding = 6;
    private const int ChartBottomPadding = 8;

    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("fr-FR");

    [Parameter]
    public long Value { get; set; }

    [Parameter]
    public string? PercentageText { get; set; }

    [Parameter]
    public string Label { get; set; } = string.Empty;

    [Parameter]
    public StatCardAccent Accent { get; set; } = StatCardAccent.Neutral;

    [Parameter]
    public SparklineShape Shape { get; set; } = SparklineShape.Flat;

    // Série horaire réelle (typiquement 24 points, une valeur par heure sur les dernières 24h, la plus
    // ancienne en premier ; voir DnsStatisticsSnapshot.HourlySeries). Quand elle contient au moins deux
    // valeurs distinctes, elle remplace le tracé de repli basé sur Shape. Sinon (null, un seul point, ou
    // toutes les valeurs identiques, par ex. toujours à 0 pour une statistique non encore implémentée), le
    // tracé de repli reste utilisé.
    [Parameter]
    public IReadOnlyList<long>? Series { get; set; }

    private string FormattedValue => Value.ToString("N0", DisplayCulture);

    private string AccentCssClass => Accent switch
    {
        StatCardAccent.Orange => "accent-orange",
        StatCardAccent.Purple => "accent-purple",
        _ => "accent-neutral",
    };

    // Au moins deux points : en dessous, aucune ligne n'est représentable (voir BuildLinePath).
    private bool HasSeries => Series is { Count: > 1 };

    private bool HasVariableSeries => HasSeries && Series!.Min() != Series!.Max();

    // Dès qu'une série réelle est fournie (même plate, ex. 0 blocage sur 24h), c'est elle qui pilote le
    // tracé : le tracé de repli basé sur Shape ne doit servir que pour les cartes sans aucune donnée (voir
    // commentaire sur Series plus haut). Sinon une carte à données réelles constantes affichait à tort
    // l'ancienne courbe factice, qui ne bougeait jamais au fil des actualisations.
    private string LinePath => HasSeries ? BuildLinePath(Series!) : FallbackLinePath;

    // Aplat sous la courbe seulement quand la série a une réelle variation, pour garder l'esthétique
    // "plate" (pas de remplissage) d'une carte dont la valeur ne bouge pas sur la fenêtre affichée.
    private string AreaPath => HasVariableSeries
        ? BuildAreaPath(Series!)
        : (HasSeries ? string.Empty : FallbackAreaPath);

    private string FallbackLinePath => Shape == SparklineShape.Spike
        ? "M0,45 C40,44 70,46 100,44 C130,42 150,45 170,40 C190,35 200,20 220,14 C235,9 245,18 260,15 C275,12 285,20 300,18"
        : "M0,50 L300,50";

    private string FallbackAreaPath => Shape == SparklineShape.Spike
        ? "M0,45 C40,44 70,46 100,44 C130,42 150,45 170,40 C190,35 200,20 220,14 C235,9 245,18 260,15 C275,12 285,20 300,18 L300,60 L0,60 Z"
        : string.Empty;

    // Convertit la série horaire en tracé SVG linéaire (segments droits, pas de lissage de courbe : calcul
    // volontairement simple pour rester léger sur un Raspberry Pi), normalisé sur toute la largeur/hauteur
    // du graphique — le minimum de la série touche le bas, le maximum touche le haut.
    private static string BuildLinePath(IReadOnlyList<long> series)
    {
        StringBuilder builder = new StringBuilder();
        IReadOnlyList<(double X, double Y)> points = ToChartPoints(series);

        for (int i = 0; i < points.Count; i++)
        {
            (double x, double y) = points[i];
            builder.Append(i == 0 ? "M" : " L");
            builder.Append(x.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(y.ToString("0.##", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string BuildAreaPath(IReadOnlyList<long> series)
    {
        string linePath = BuildLinePath(series);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{linePath} L{ChartWidth},{ChartHeight} L0,{ChartHeight} Z");
    }

    private static IReadOnlyList<(double X, double Y)> ToChartPoints(IReadOnlyList<long> series)
    {
        long min = series.Min();
        long max = series.Max();
        long range = max - min;
        double usableHeight = ChartHeight - ChartTopPadding - ChartBottomPadding;
        double step = series.Count > 1 ? (double)ChartWidth / (series.Count - 1) : 0;

        List<(double X, double Y)> points = new List<(double X, double Y)>(series.Count);

        for (int i = 0; i < series.Count; i++)
        {
            double x = step * i;
            // Série plate (range == 0, ex. toujours 0 sur la fenêtre) : ligne horizontale à mi-hauteur
            // plutôt qu'une division par zéro.
            double normalized = range > 0 ? (series[i] - min) / (double)range : 0.5;
            double y = ChartHeight - ChartBottomPadding - (normalized * usableHeight);
            points.Add((x, y));
        }

        return points;
    }
}
