using System;
using System.Collections.Generic;

namespace OnyxFilter.Services.ClientLocation;

// Continent de chaque pays (codes ISO 3166-1 alpha-2), selon le découpage de GeoNames : l'Amérique
// centrale et les Caraïbes font partie de l'Amérique du Nord, la Russie et Chypre de l'Europe, la Turquie
// et le Caucase de l'Asie. Sert à regrouper les pays de la page "Filtrage par pays".
public static class Continents
{
    // Libellés français, utilisés comme clés de traduction (L[...]) ; dans l'ordre d'affichage.
    public const string Africa = "Afrique";
    public const string NorthAmerica = "Amérique du Nord";
    public const string SouthAmerica = "Amérique du Sud";
    public const string Asia = "Asie";
    public const string Europe = "Europe";
    public const string Oceania = "Océanie";
    public const string Antarctica = "Antarctique";
    public const string Other = "Autres";

    public static readonly IReadOnlyList<string> All = [Europe, NorthAmerica, SouthAmerica, Africa, Asia, Oceania, Antarctica, Other];

    private static readonly Dictionary<string, string> ByCountry = Build(new Dictionary<string, string>
    {
        [Africa] = "AO BF BI BJ BW CD CF CG CI CM CV DJ DZ EG EH ER ET GA GH GM GN GQ GW KE KM LR LS LY MA MG ML MR MU MW MZ NA NE NG RE RW SC SD SH SL SN SO SS ST SZ TD TG TN TZ UG YT ZA ZM ZW",
        [NorthAmerica] = "AG AI AW BB BL BM BQ BS BZ CA CR CU CW DM DO GD GL GP GT HN HT JM KN KY LC MF MQ MS MX NI PA PM PR SV SX TC TT US VC VG VI",
        [SouthAmerica] = "AR BO BR CL CO EC FK GF GY PE PY SR UY VE",
        [Asia] = "AE AF AM AZ BD BH BN BT CC CN CX GE HK ID IL IN IO IQ IR JO JP KG KH KP KR KW KZ LA LB LK MM MN MO MV MY NP OM PH PK PS QA SA SG SY TH TJ TM TR TW UZ VN YE",
        [Europe] = "AD AL AT AX BA BE BG BY CH CY CZ DE DK EE ES FI FO FR GB GG GI GR HR HU IE IM IS IT JE LI LT LU LV MC MD ME MK MT NL NO PL PT RO RS RU SE SI SJ SK SM UA VA XK",
        [Oceania] = "AS AU CK FJ FM GU KI MH MP NC NF NR NU NZ PF PG PN PW SB TK TL TO TV UM VU WF WS",
        [Antarctica] = "AQ BV GS HM TF",
    });

    // Continent d'un code pays ; Other si le code est inconnu.
    public static string Of(string countryCode)
    {
        return ByCountry.TryGetValue(countryCode, out string? continent) ? continent : Other;
    }

    private static Dictionary<string, string> Build(Dictionary<string, string> countriesByContinent)
    {
        Dictionary<string, string> byCountry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string continent, string codes) in countriesByContinent)
        {
            foreach (string code in codes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                byCountry.Add(code, continent);
            }
        }

        return byCountry;
    }
}
