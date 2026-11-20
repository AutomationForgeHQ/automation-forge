using Forge.Core.Engines;

namespace Forge.Core.Projects;

/// <summary>How the Projects page orders its tiles.</summary>
public enum ProjectSort
{
    /// <summary>The project opened last in Unreal first; projects Unreal never recorded as opened last, by name.</summary>
    RecentlyOpened,
    Name,
    /// <summary>The newest Unreal Engine first; a project whose engine cannot be told last.</summary>
    EngineVersion,
}

/// <summary>
/// The pure parts of the Projects page's grid (0.5.0-beta.4): a project's picture, the initials shown when it has
/// none, the search and the orders. No UI, no decoding: the window loads the picture it is told about.
/// </summary>
public static class ProjectGallery
{
    /// <summary>
    /// The picture of a project, as the Epic launcher shows it: <c>&lt;Name&gt;.png</c> beside the .uproject (the
    /// thumbnail Unreal writes from "Project Settings → Description → Project Thumbnail"), else Unreal's own
    /// <c>Saved\AutoScreenshot.png</c>, else null. Only whether a file is there is asked, never what it holds: a
    /// file that does not decode is the window's to fall back on.
    /// </summary>
    public static string? PictureOf(string uproject)
    {
        if (string.IsNullOrWhiteSpace(uproject)) return null;
        try
        {
            var folder = Path.GetDirectoryName(uproject);
            if (string.IsNullOrEmpty(folder)) return null;
            var own = Path.Combine(folder, Path.GetFileNameWithoutExtension(uproject) + ".png");
            if (IsFile(own)) return own;
            var auto = Path.Combine(folder, "Saved", "AutoScreenshot.png");
            return IsFile(auto) ? auto : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0;
    }

    /// <summary>
    /// Two letters for a project without a picture: the first letters of its first two words ("Colony_NP24" is
    /// "CN", "MyGame" is "MG"), or the first two letters of a one-word name ("Studio" is "ST").
    /// </summary>
    public static string Initials(string name)
    {
        var words = Words(name).ToList();
        if (words.Count >= 2) return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
        if (words.Count == 1) return words[0].Length >= 2 ? words[0][..2].ToUpperInvariant() : words[0].ToUpperInvariant();
        return "?";
    }

    /// <summary>The words of a name: split at '_', '-', '.', spaces, and where a lower-case letter meets a capital.</summary>
    private static IEnumerable<string> Words(string name)
    {
        var word = new System.Text.StringBuilder();
        char prev = '\0';
        foreach (var ch in name ?? "")
        {
            if (!char.IsLetterOrDigit(ch))
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                prev = '\0';
                continue;
            }
            if (word.Length > 0 && char.IsUpper(ch) && char.IsLower(prev)) { yield return word.ToString(); word.Clear(); }
            word.Append(ch);
            prev = ch;
        }
        if (word.Length > 0) yield return word.ToString();
    }

    /// <summary>Whether a project answers a search: its name or its folder holds the text, in any case. Empty text matches all.</summary>
    public static bool Matches(ProjectReport project, string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return project.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || project.Found.Folder.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The projects in the page's order; ties by name, then by path, so the order never jumps between refreshes.</summary>
    public static IEnumerable<T> Sort<T>(IEnumerable<T> items, Func<T, ProjectReport> report, ProjectSort sort)
    {
        var byName = StringComparer.OrdinalIgnoreCase;
        return sort switch
        {
            ProjectSort.RecentlyOpened => items
                .OrderBy(i => report(i).Found.LastOpened is null)
                .ThenByDescending(i => report(i).Found.LastOpened ?? DateTime.MinValue)
                .ThenBy(i => report(i).Name, byName).ThenBy(i => report(i).Path, byName),
            ProjectSort.EngineVersion => items
                .OrderBy(i => report(i).Engine.Version is null)
                .ThenByDescending(i => report(i).Engine.Version ?? "", VersionOrder.Instance)
                .ThenBy(i => report(i).Name, byName).ThenBy(i => report(i).Path, byName),
            _ => items.OrderBy(i => report(i).Name, byName).ThenBy(i => report(i).Path, byName),
        };
    }

    /// <summary>"5.8" on a tile's picture; "5.7 · not installed" when that engine is not on this computer; "?" when it cannot be told.</summary>
    public static string EngineBadge(ResolvedEngine engine)
    {
        var v = engine.Engine?.Version ?? engine.Version;
        if (v is null) return engine.Kind == AssociationKind.SourceBuild ? "SOURCE BUILD · not found" : "ENGINE ?";
        return engine.IsInstalled ? v : $"{v} · not installed";
    }
}
