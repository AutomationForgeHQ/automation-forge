using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Core.Manifest;

namespace Forge.Core.Releases;

/// <summary>
/// The releases repository is the truth about what is downloadable. The manifest
/// describes what each plugin is; this reads what has actually been published
/// and merges any version the manifest does not know yet, so the hub is never
/// behind a regenerated manifest. Anonymous GitHub access with an ETag cache,
/// so repeated checks cost no rate limit and work offline.
/// </summary>
public sealed class ReleaseDiscovery
{
    public const string DefaultApi = "https://api.github.com/repos/AutomationForgeHQ/releases/releases?per_page=100";

    private static readonly Regex Tag = new(@"^(?<plugin>[a-z0-9]+)-v(?<ver>.+)$", RegexOptions.Compiled);
    private static readonly Regex Asset = new(@"^(?<plugin>[A-Za-z0-9]+)-(?<version>.+?)-UE(?<engine>\d+\.\d+)-(?<platform>Win64|Mac|Linux)(?<symbols>-symbols)?\.zip$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly string _api;
    private readonly string _cacheJson;
    private readonly string _cacheEtag;

    public ReleaseDiscovery(HttpClient http, string? api = null, string? cacheDir = null)
    {
        _http = http;
        _api = api ?? Environment.GetEnvironmentVariable("FORGE_RELEASES_API") ?? DefaultApi;
        var dir = cacheDir ?? Paths.DataDir;
        _cacheJson = Path.Combine(dir, "releases.json");
        _cacheEtag = Path.Combine(dir, "releases.etag");
    }

    public sealed record Result(int Releases, int Merged, bool FromCache, DateTimeOffset CheckedAt);

    public async Task<Result> MergeIntoAsync(Manifest.Manifest manifest, bool offline = false, CancellationToken ct = default)
    {
        var (json, fromCache) = await FetchAsync(offline, ct);
        if (json is null) return new Result(0, 0, true, DateTimeOffset.UtcNow);

        var byLower = manifest.Plugins.ToDictionary(p => p.Id.ToLowerInvariant(), p => p);
        int releases = 0, merged = 0;
        using var doc = JsonDocument.Parse(json);
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            var tag = rel.GetProperty("tag_name").GetString() ?? "";
            var tm = Tag.Match(tag);
            if (!tm.Success || !byLower.TryGetValue(tm.Groups["plugin"].Value, out var plugin)) continue;
            releases++;
            var prerelease = rel.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
            var publishedAt = rel.TryGetProperty("published_at", out var pa) ? pa.GetString() ?? "" : "";
            var notes = rel.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;
            if (!rel.TryGetProperty("assets", out var assets)) continue;

            var symbolsByBase = new Dictionary<string, string>();
            var packages = new List<(Match m, JsonElement a)>();
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var am = Asset.Match(name);
                if (!am.Success) continue;
                if (am.Groups["symbols"].Success)
                    symbolsByBase[name.Replace("-symbols.zip", ".zip")] = a.GetProperty("browser_download_url").GetString() ?? "";
                else
                    packages.Add((am, a));
            }
            foreach (var (am, a) in packages)
            {
                var version = am.Groups["version"].Value;
                var engine = am.Groups["engine"].Value;
                var platform = am.Groups["platform"].Value;
                if (plugin.Versions.Any(v => v.Version == version && v.Engine == engine && v.Platform == platform)) continue;
                var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
                plugin.Versions.Add(new VersionInfo
                {
                    Version = version, Engine = engine, Platform = platform,
                    Channel = prerelease ? "nightly" : "stable",
                    Url = a.GetProperty("browser_download_url").GetString(),
                    Size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    Sha256 = digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : digest,
                    Symbols = symbolsByBase.GetValueOrDefault(a.GetProperty("name").GetString() ?? ""),
                    ReleasedAt = publishedAt, Notes = notes,
                });
                merged++;
            }
        }
        foreach (var p in manifest.Plugins)
            p.Versions.Sort((x, y) => string.CompareOrdinal(y.ReleasedAt, x.ReleasedAt));
        return new Result(releases, merged, fromCache, DateTimeOffset.UtcNow);
    }

    private async Task<(string? json, bool fromCache)> FetchAsync(bool offline, CancellationToken ct)
    {
        var cached = File.Exists(_cacheJson) ? await File.ReadAllTextAsync(_cacheJson, ct) : null;
        if (offline) return (cached, true);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _api);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (cached is not null && File.Exists(_cacheEtag))
                req.Headers.TryAddWithoutValidation("If-None-Match", await File.ReadAllTextAsync(_cacheEtag, ct));
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotModified && cached is not null) return (cached, true);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheJson)!);
            await File.WriteAllTextAsync(_cacheJson, json, ct);
            if (resp.Headers.ETag is { } etag) await File.WriteAllTextAsync(_cacheEtag, etag.ToString(), ct);
            return (json, false);
        }
        catch (Exception) when (cached is not null)
        {
            return (cached, true);
        }
    }
}
