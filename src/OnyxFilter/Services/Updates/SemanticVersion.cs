using System;
using System.Globalization;
using System.Linq;

namespace OnyxFilter.Services.Updates;

// Numéro de version au format SemVer 2.0 (MAJEUR.MINEUR.CORRECTIF[-préversion][+métadonnées]), tel que
// porté par les tags des publications GitHub (« v1.4.0 », « v1.5.0-beta.2 ») et par la version
// informative de l'assembly (« 1.4.0+3f2a9c1 »). Les métadonnées de build sont ignorées à la comparaison.
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, string? preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    // Identifiants de préversion (« beta.2 »), ou null pour une version stable.
    public string? PreRelease { get; }

    public bool IsPreRelease => PreRelease is not null;

    // Préversion « dev » : compilation locale, hors publication (voir <Version> du .csproj).
    public bool IsDevelopmentBuild => PreRelease is not null && PreRelease.StartsWith("dev", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = null!;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();

        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value.Substring(1);
        }

        int buildIndex = value.IndexOf('+');
        if (buildIndex >= 0)
        {
            value = value.Substring(0, buildIndex);
        }

        string? preRelease = null;
        int preReleaseIndex = value.IndexOf('-');
        if (preReleaseIndex >= 0)
        {
            preRelease = value.Substring(preReleaseIndex + 1);
            value = value.Substring(0, preReleaseIndex);

            if (preRelease.Length == 0 || preRelease.Split('.').Any(identifier => identifier.Length == 0))
            {
                return false;
            }
        }

        string[] parts = value.Split('.');
        if (parts.Length != 3
            || !TryParseNumber(parts[0], out int major)
            || !TryParseNumber(parts[1], out int minor)
            || !TryParseNumber(parts[2], out int patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        // À numéro égal, une version stable est plus récente que toutes ses préversions.
        if (PreRelease is null || other.PreRelease is null)
        {
            return PreRelease is null ? (other.PreRelease is null ? 0 : 1) : -1;
        }

        string[] left = PreRelease.Split('.');
        string[] right = other.PreRelease.Split('.');

        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            bool leftNumeric = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out long leftNumber);
            bool rightNumeric = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out long rightNumber);

            if (leftNumeric && rightNumeric)
            {
                result = leftNumber.CompareTo(rightNumber);
            }
            else if (leftNumeric != rightNumeric)
            {
                // Identifiant numérique < identifiant alphanumérique.
                result = leftNumeric ? -1 : 1;
            }
            else
            {
                result = string.CompareOrdinal(left[i], right[i]);
            }

            if (result != 0)
            {
                return Math.Sign(result);
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    public override string ToString()
    {
        string core = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        return PreRelease is null ? core : core + "-" + PreRelease;
    }

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    private static bool TryParseNumber(string text, out int number)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}
