// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace LinFan.App.Services;

/// <summary>
/// Minimaler SemVer-Vergleich für den Update-Check: Core-Version X.Y.Z plus optionales Prerelease-Suffix
/// (Build-Metadaten nach '+' werden verworfen, ein führendes 'v' ebenso). Bewusst kein NuGet — nur so viel,
/// wie „ist Release Y neuer als die laufende Version?" braucht. Präzedenz nach semver.org: höhere Core-Zahl
/// gewinnt; bei gleichem Core zählt eine Version OHNE Prerelease als höher (<c>0.1.0 &gt; 0.1.0-dev</c>).
/// </summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemVer>
{
    /// <summary>Parst „[v]X[.Y[.Z]][-prerelease][+build]"; fehlende Stellen sind 0. Wirft nie.</summary>
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string s = text.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            s = s[1..];

        int plus = s.IndexOf('+'); // Build-Metadaten abschneiden
        if (plus >= 0)
            s = s[..plus];

        string? pre = null;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
        }

        string[] parts = s.Split('.');
        if (parts.Length is < 1 or > 3)
            return false;

        int[] nums = new int[3];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
                return false;

        version = new SemVer(nums[0], nums[1], nums[2], string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    public int CompareTo(SemVer other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // Gleicher Core: eine Release-Version (kein Prerelease) rangiert über einer Prerelease; sonst grob
        // ordinal (reicht für „dev"/„rc1" — der Update-Check braucht keine volle Identifier-Sortierung).
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public override string ToString() =>
        PreRelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
