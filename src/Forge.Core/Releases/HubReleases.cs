using System.Text.Json;

namespace Forge.Core.Releases;

/// <summary>One release of the hub and CLI: the tag, and the installer asset if the release carries one.</summary>
public sealed record HubRelease(
    SemVer Version, string Tag, bool Prerelease, string HtmlUrl, string PublishedAt,
    string? InstallerUrl, string? InstallerSha256, long InstallerSize);

/// <summary>
/// The hub's own releases, from the automation-forge repository — the same
/// discipline as the plugins: anonymous, ETag-cached, offline-tolerant. A
/// GitHub pre-release is a nightly; the stable channel never sees one.
/// </summary>
public sealed class HubReleases
{
    public const string DefaultApi = "https://api.github.com/repos/AutomationForgeHQ/automation-forge/releases?per_page=30";
    public const string InstallerAsset = "AutomationForge-Setup.exe";

    private readonly GitHubJsonCache _cache;
    private readonly string _api;

    public HubReleases(HttpClient http, string? api = null, string? cacheDir = null)
    {
        _api = api ?? Environment.GetEnvironmentVariable("FORGE_HUB_RELEASES_API") ?? DefaultApi;
        _cache = new GitHubJsonCache(http, "hub-releases", cacheDir);
    }

    public async Task<IReadOnlyList<HubRelease>> ListAsync(bool offline = false, CancellationToken ct = default)
    {
        var (json, _) = await _cache.FetchAsync(_api, offline, ct);
        if (json is null) return [];
        var list = new List<HubRelease>();
        using var doc = JsonDocument.Parse(json);
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var tag = rel.GetProperty("tag_name").GetString() ?? "";
            if (!SemVer.TryParse(tag, out var version)) continue;
            var prerelease = rel.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
            var html = rel.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";
            var published = rel.TryGetProperty("published_at", out var pa) ? pa.GetString() ?? "" : "";
            string? url = null, sha = null;
            long size = 0;
            if (rel.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.GetProperty("name").GetString() != InstallerAsset) continue;
                    url = a.GetProperty("browser_download_url").GetString();
                    size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    sha = digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : digest;
                }
            }
            list.Add(new HubRelease(version, tag, prerelease, html, published, url, sha, size));
        }
        return list;
    }

    /// <summary>The newest release on a channel that is newer than what is running, or null when this is current.</summary>
    public async Task<HubRelease?> NewerThanAsync(SemVer current, string channel, bool offline = false, CancellationToken ct = default)
    {
        var all = await ListAsync(offline, ct);
        var best = all.Where(r => channel == Settings.Nightly || !r.Prerelease)
                      .OrderByDescending(r => r.Version)
                      .FirstOrDefault();
        return best is not null && best.Version > current ? best : null;
    }
}
