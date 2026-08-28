namespace Forge.Core;

/// <summary>
/// Enough of semver 2.0 for our tags: 1.2.3, and 1.2.3-nightly.20260901. A
/// leading v is accepted and build metadata after + is ignored. A pre-release
/// sorts below the release it precedes, so 0.3.0-nightly.x sits between 0.2.0
/// and 0.3.0 — which is exactly where a nightly belongs.
/// </summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? Pre) : IComparable<SemVer>
{
    public bool IsPrerelease => Pre is not null;

    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0) { pre = s[(dash + 1)..]; s = s[..dash]; }
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 3) return false;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return false;
        var patch = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], out patch)) return false;
        version = new SemVer(major, minor, patch, string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    public static SemVer Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Not a version: {text}");

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor); if (c != 0) return c;
        c = Patch.CompareTo(other.Patch); if (c != 0) return c;
        if (Pre is null) return other.Pre is null ? 0 : 1;
        if (other.Pre is null) return -1;
        return ComparePre(Pre, other.Pre);
    }

    private static int ComparePre(string a, string b)
    {
        var xs = a.Split('.');
        var ys = b.Split('.');
        for (var i = 0; i < Math.Min(xs.Length, ys.Length); i++)
        {
            var xn = long.TryParse(xs[i], out var xv);
            var yn = long.TryParse(ys[i], out var yv);
            var c = xn && yn ? xv.CompareTo(yv) : xn ? -1 : yn ? 1 : string.CompareOrdinal(xs[i], ys[i]);
            if (c != 0) return c;
        }
        return xs.Length.CompareTo(ys.Length);
    }

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;

    public override string ToString() => Pre is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";
}
