using System.Text.Json;

namespace Forge.Core.Releases;

/// <summary>One release of the hub and CLI: the tag, and the installer asset if the release carries one.</summary>
public sealed record HubRelease(
    SemVer Version, string Tag, bool Prerelease, string HtmlUrl, string PublishedAt,
    string? InstallerUrl, string? InstallerSha256, long InstallerSize);

/// <summary>
/// The hub's own releases, from the automation-forge repository: anonymous, ETag-cached, offline-tolerant.
///
/// The repository holds two lines of releases. The Automation Forge Hub 0.4.x is tagged v&lt;semver&gt; and ships
/// AutomationForge-Setup.exe; Kovati Hub v2 is tagged kovati-hub-v&lt;semver&gt; and ships KovatiHub-Setup.exe
/// (<see cref="HubIdentity"/>). This Hub reads only its own line: a release counts when its tag starts with the
/// prefix, the rest parses as a version, and it carries the installer asset by name. Anything else, a v0.4.5 among
/// them, is somebody else's.
///
/// A GitHub pre-release is a nightly or a beta. The stable channel skips them, except when the Hub running is a
/// pre-release itself: a beta follows its own line, or it would never hear of the next beta.
/// </summary>
public sealed class HubReleases
{
    public const string DefaultApi = "https://api.github.com/repos/AutomationForgeHQ/automation-forge/releases?per_page=30";
    public const string InstallerAsset = HubIdentity.InstallerAsset;
    public const string TagPrefix = HubIdentity.ReleaseTagPrefix;

    private readonly GitHubJsonCache _cache;
    private readonly string _api;

    public HubReleases(HttpClient http, string? api = null, string? cacheDir = null)
    {
        _api = api ?? Environment.GetEnvironmentVariable("FORGE_HUB_RELEASES_API") ?? DefaultApi;
        _cache = new GitHubJsonCache(http, HubIdentity.ReleasesCacheName, cacheDir);
    }

    public async Task<IReadOnlyList<HubRelease>> ListAsync(bool offline = false, CancellationToken ct = default)
    {
        if (_api == "off") return [];
        var (json, _) = await _cache.FetchAsync(_api, offline, ct);
        return json is null ? [] : Parse(json);
    }

    /// <summary>
    /// The releases of one line in a GitHub release list. An empty prefix reads the list the way 0.4.x does: every
    /// tag that parses as a version, with <paramref name="asset"/> attached when present.
    /// </summary>
    public static IReadOnlyList<HubRelease> Parse(string json, string tagPrefix = TagPrefix, string asset = InstallerAsset)
    {
        var list = new List<HubRelease>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var tag = rel.TryGetProperty("tag_name", out var tn) ? tn.GetString() ?? "" : "";
            var versionText = tag;
            if (tagPrefix.Length > 0)
            {
                if (!tag.StartsWith(tagPrefix, StringComparison.Ordinal)) continue;
                versionText = tag[tagPrefix.Length..];
                // "kovati-hub-v" then a digit: no second v, no empty version.
                if (versionText.Length == 0 || !char.IsAsciiDigit(versionText[0])) continue;
            }
            if (!SemVer.TryParse(versionText, out var version)) continue;
            var prerelease = rel.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
            var html = rel.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";
            var published = rel.TryGetProperty("published_at", out var pa) ? pa.GetString() ?? "" : "";
            string? url = null, sha = null;
            long size = 0;
            if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.TryGetProperty("name", out var n) && n.GetString() != asset) continue;
                    url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    sha = digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : digest;
                }
            }
            // This line's releases must carry its installer: without it there is nothing to update from.
            if (tagPrefix.Length > 0 && url is null) continue;
            list.Add(new HubRelease(version, tag, prerelease, html, published, url, sha, size));
        }
        return list;
    }

    /// <summary>The newest release on a channel that is newer than what is running, or null when this is current.</summary>
    public async Task<HubRelease?> NewerThanAsync(SemVer current, string channel, bool offline = false, CancellationToken ct = default) =>
        Newest(await ListAsync(offline, ct), current, channel);

    public static HubRelease? Newest(IEnumerable<HubRelease> releases, SemVer current, string channel)
    {
        var best = releases.Where(r => channel == Settings.Nightly || !r.Prerelease || current.IsPrerelease)
                           .OrderByDescending(r => r.Version)
                           .FirstOrDefault();
        return best is not null && best.Version > current ? best : null;
    }
}
