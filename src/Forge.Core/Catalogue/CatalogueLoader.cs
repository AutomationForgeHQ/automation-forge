namespace Forge.Core.Catalogue;

/// <summary>
/// Finds the catalogue (12.5). Sources, tried together: the copy cached in the data folder, the one shipped
/// beside the program, and FORGE_CATALOGUE (a file, for local work). The newest generatedAt whose schema this
/// build understands wins; a newer schema is ignored, never half-read.
///
/// The network copy reaches this through the cache: <see cref="CatalogueClient"/> writes what the site serves
/// to <see cref="CachePath"/>, and the next Load weighs it against the bundled copy like any other.
/// </summary>
public static class CatalogueLoader
{
    /// <summary>The installer's copy, beside the executable: catalogue\catalogue.json.</summary>
    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "catalogue", "catalogue.json");

    public static string CachePath => Path.Combine(Paths.DataDir, "catalogue.json");

    /// <summary>The folder the bundled stills live in, with the site's relative paths under it.</summary>
    public static string BundledMediaRoot => Path.Combine(AppContext.BaseDirectory, "catalogue");

    /// <summary>Why the last file could not be read, for the Details log.</summary>
    public static string? LastError { get; private set; }

    public static Catalogue? Load(string? extraPath = null)
    {
        // An explicit file wins outright: it is what a developer pointed the app at.
        foreach (var explicitPath in new[] { Environment.GetEnvironmentVariable("FORGE_CATALOGUE"), extraPath })
            if (!string.IsNullOrWhiteSpace(explicitPath) && TryRead(explicitPath, explicitPath) is { } chosen)
                return chosen;

        Catalogue? best = null;
        foreach (var (path, source) in new[] { (CachePath, "cache"), (BundledPath, "bundled") })
        {
            var c = TryRead(path, source);
            if (c is null) continue;
            if (best is null || IsNewer(c.GeneratedAt, best.GeneratedAt)) best = c;
        }
        return best;
    }

    /// <summary>generatedAt a is later than b: as times when both read as times, else by their text.</summary>
    public static bool IsNewer(string? a, string? b)
    {
        var styles = System.Globalization.DateTimeStyles.AssumeUniversal;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (DateTimeOffset.TryParse(a, inv, styles, out var ta) && DateTimeOffset.TryParse(b, inv, styles, out var tb)) return ta > tb;
        return string.CompareOrdinal(a ?? "", b ?? "") > 0;
    }

    /// <summary>One file, or null when it is missing, unreadable, or of a schema this build does not understand.</summary>
    public static Catalogue? TryRead(string path, string source)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var c = Catalogue.Parse(File.ReadAllText(path), source);
            return c.SchemaVersion is >= 1 and <= Catalogue.SupportedSchema ? c : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            LastError = $"{path}: {ex.Message}";
            return null;
        }
    }
}
