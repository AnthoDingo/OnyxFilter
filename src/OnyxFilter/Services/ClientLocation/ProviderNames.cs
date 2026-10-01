using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OnyxFilter.Services.ClientLocation;

// Nom lisible d'un fournisseur d'accès à partir de la description brute de son système autonome (ASN),
// souvent un identifiant technique ("PROXAD", "IONOS-AS This is the joint network for…").
public static partial class ProviderNames
{
    private const int MaxLength = 40;

    // Noms commerciaux des opérateurs les plus courants, dont l'identifiant technique ne dit rien.
    private static readonly Dictionary<int, string> KnownProviders = new Dictionary<int, string>
    {
        // France
        [12322] = "Free",
        [51207] = "Free Mobile",
        [3215] = "Orange",
        [15557] = "SFR",
        [21502] = "SFR",
        [5410] = "Bouygues Telecom",
        [16276] = "OVHcloud",
        [12876] = "Scaleway",
        // Europe
        [8560] = "IONOS",
        [3320] = "Deutsche Telekom",
        [6805] = "Telefónica Germany",
        [8881] = "1&1 Versatel",
        [24940] = "Hetzner",
        [3352] = "Telefónica",
        [2856] = "BT",
        [5089] = "Virgin Media",
        [1136] = "KPN",
        [6830] = "Liberty Global",
        [5432] = "Proximus",
        [6848] = "Telenet",
        [3303] = "Swisscom",
        // Amérique du Nord
        [7922] = "Comcast",
        [577] = "Bell Canada",
        [5769] = "Vidéotron",
        // Cloud et réseaux de diffusion
        [13335] = "Cloudflare",
        [15169] = "Google",
        [396982] = "Google Cloud",
        [16509] = "Amazon AWS",
        [14618] = "Amazon AWS",
        [8075] = "Microsoft",
        [32934] = "Meta",
        [14061] = "DigitalOcean",
        [20940] = "Akamai",
        [54113] = "Fastly",
        [174] = "Cogent",
        [3356] = "Lumen",
        [2914] = "NTT",
    };

    public static string Resolve(int asn, string description)
    {
        if (KnownProviders.TryGetValue(asn, out string? knownName))
        {
            return knownName;
        }

        string trimmed = description.Trim();
        int spaceIndex = trimmed.IndexOf(' ');
        string handle = spaceIndex < 0 ? trimmed : trimmed.Substring(0, spaceIndex);

        // Description de la forme "IDENTIFIANT Nom en clair" : le nom en clair est retenu s'il est court.
        if (spaceIndex > 0 && HandlePattern().IsMatch(handle))
        {
            string rest = trimmed.Substring(spaceIndex + 1).Trim(' ', '-');

            if (rest.Length >= 3 && rest.Length <= MaxLength)
            {
                return rest;
            }

            return Shorten(CleanHandle(handle));
        }

        return Shorten(spaceIndex < 0 ? CleanHandle(trimmed) : trimmed);
    }

    // "IDNIC-OLEAN-AS-ID" -> "OLEAN", "COMCAST-7922" -> "COMCAST", "DIGITALOCEAN-ASN" -> "DIGITALOCEAN".
    private static string CleanHandle(string handle)
    {
        string cleaned = HandleSuffixPattern().Replace(HandlePrefixPattern().Replace(handle, string.Empty), string.Empty);
        return cleaned.Length == 0 ? handle : cleaned;
    }

    private static string Shorten(string value)
    {
        return value.Length <= MaxLength ? value : value.Substring(0, MaxLength - 1).TrimEnd() + "…";
    }

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9_.\-]*$")]
    private static partial Regex HandlePattern();

    [GeneratedRegex(@"^(IDNIC|ASN|AS)-")]
    private static partial Regex HandlePrefixPattern();

    [GeneratedRegex(@"(-(AS|ASN|AP|ID|NET|BLOCK|\d+))+$")]
    private static partial Regex HandleSuffixPattern();
}
