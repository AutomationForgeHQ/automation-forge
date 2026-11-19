namespace Forge.Core;

/// <summary>How mature a product is. Not the release channel: the channel decides which builds are offered.</summary>
public enum Stage
{
    Coming,
    Labs,
    Early,
    Beta,
    Released,
}

public static class Stages
{
    /// <summary>
    /// Bojan's rule (6.6), from the newest stable version of a product's first component for the target's
    /// engine: 0.0.x Labs, 0.1.x Early access, 0.2.x to 0.9.x Beta, 1.x and up Released, none Coming soon.
    /// Never from the catalogue or the descriptor's flags.
    /// </summary>
    public static Stage Of(string? version)
    {
        if (!SemVer.TryParse(version, out var v)) return Stage.Coming;
        if (v.Major >= 1) return Stage.Released;
        return v.Minor switch
        {
            0 => Stage.Labs,
            1 => Stage.Early,
            _ => Stage.Beta,
        };
    }

    /// <summary>The key the catalogue's labels use: labs, early, beta, released, coming.</summary>
    public static string Key(this Stage s) => s switch
    {
        Stage.Labs => "labs",
        Stage.Early => "early",
        Stage.Beta => "beta",
        Stage.Released => "released",
        _ => "coming",
    };

    public static string DefaultLabel(this Stage s) => s switch
    {
        Stage.Labs => "Labs",
        Stage.Early => "Early access",
        Stage.Beta => "Beta",
        Stage.Released => "Released",
        _ => "Coming soon",
    };
}
