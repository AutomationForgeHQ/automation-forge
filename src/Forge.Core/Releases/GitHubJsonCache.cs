using System.Net;

namespace Forge.Core.Releases;

/// <summary>
/// Anonymous GitHub JSON with an ETag cache. A repeated check that has not
/// changed costs no rate limit (304), and when the network is away the last
/// good copy serves. One instance per document, named for its cache file.
/// </summary>
public sealed class GitHubJsonCache
{
    private readonly HttpClient _http;
    private readonly string _cacheJson;
    private readonly string _cacheEtag;

    /// <summary>Whether the last fetch brought a document different from the cached one.</summary>
    public bool LastChanged { get; private set; }

    public GitHubJsonCache(HttpClient http, string name, string? cacheDir = null)
    {
        _http = http;
        var dir = cacheDir ?? Paths.DataDir;
        _cacheJson = Path.Combine(dir, $"{name}.json");
        _cacheEtag = Path.Combine(dir, $"{name}.etag");
    }

    public async Task<(string? json, bool fromCache)> FetchAsync(string url, bool offline = false, CancellationToken ct = default)
    {
        LastChanged = false;
        var cached = await ReadOrNullAsync(_cacheJson, ct);
        if (offline) return (cached, true);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (cached is not null && await ReadOrNullAsync(_cacheEtag, ct) is { Length: > 0 } etag)
                req.Headers.TryAddWithoutValidation("If-None-Match", etag.Trim());
            using var resp = await _http.SendAsync(req, ct);
            // 304 is the network confirming the copy is current — not "offline".
            if (resp.StatusCode == HttpStatusCode.NotModified && cached is not null) return (cached, false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            LastChanged = json != cached;
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheJson)!);
            // Atomically: another Hub may be reading the same cache.
            Catalogue.CatalogueClient.WriteAtomically(_cacheJson, json);
            if (resp.Headers.ETag is { } tag) Catalogue.CatalogueClient.WriteAtomically(_cacheEtag, tag.ToString());
            else if (File.Exists(_cacheEtag)) File.Delete(_cacheEtag);
            return (json, false);
        }
        catch (Exception) when (cached is not null)
        {
            return (cached, true);
        }
    }

    private static async Task<string?> ReadOrNullAsync(string path, CancellationToken ct)
    {
        try { return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null; }
        catch (IOException) { return null; }
    }
}
