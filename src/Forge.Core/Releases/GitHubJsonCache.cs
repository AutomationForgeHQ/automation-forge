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

    public GitHubJsonCache(HttpClient http, string name, string? cacheDir = null)
    {
        _http = http;
        var dir = cacheDir ?? Paths.DataDir;
        _cacheJson = Path.Combine(dir, $"{name}.json");
        _cacheEtag = Path.Combine(dir, $"{name}.etag");
    }

    public async Task<(string? json, bool fromCache)> FetchAsync(string url, bool offline = false, CancellationToken ct = default)
    {
        var cached = File.Exists(_cacheJson) ? await File.ReadAllTextAsync(_cacheJson, ct) : null;
        if (offline) return (cached, true);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
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
