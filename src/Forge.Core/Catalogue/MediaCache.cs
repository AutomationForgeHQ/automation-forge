using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Forge.Core.Catalogue;

/// <summary>
/// The pictures the catalogue names, kept on disk (12.4): %LOCALAPPDATA%\AutomationForge\media\, one file per
/// site address. The key is a hash of the whole path as the catalogue writes it, ?v= stamp included, so a
/// replaced picture is a new address and is fetched again; a cached file is never asked for twice.
///
/// Downloads are off the caller's thread, at most <see cref="Parallel"/> at a time, one per address however
/// many controls ask, written to a temporary file and moved into place. Only the site's own host is asked, over
/// http or https, only for a path under it; only an image answer is kept, and none larger than
/// <see cref="MaxFileBytes"/>. The folder is held under <see cref="MaxTotalBytes"/> by removing the files used
/// longest ago. A failed address is not asked again for an hour. Nothing here throws to the caller.
/// </summary>
public sealed class MediaCache
{
    public const long MaxFileBytes = 12 * 1024 * 1024;
    public const long DefaultMaxTotalBytes = 200L * 1024 * 1024;
    public const int Parallel = 3;
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    private static readonly string[] ImageTypes = ["image/jpeg", "image/png", "image/webp", "image/gif"];
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(Parallel, Parallel);
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new();
    private readonly ConcurrentDictionary<string, DateTime> _failed = new();
    private readonly object _evictLock = new();

    public string Dir { get; }
    public Uri SiteBase { get; }
    public long MaxTotalBytes { get; }

    public MediaCache(HttpClient http, string siteBase, string? dir = null, long maxTotalBytes = DefaultMaxTotalBytes)
    {
        _http = http;
        SiteBase = new Uri(SiteAddress.Normalise(siteBase) ?? SiteAddress.DefaultBase);
        Dir = dir ?? Path.Combine(Paths.DataDir, "media");
        MaxTotalBytes = maxTotalBytes;
    }

    private static readonly Lazy<MediaCache> _shared = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent("hub-media"));
        return new MediaCache(http, MediaBase);
    });

    /// <summary>The Hub's one cache, against <see cref="MediaBase"/>.</summary>
    public static MediaCache Shared => _shared.Value;

    /// <summary>Where pictures come from: FORGE_MEDIA_URL when it is an address, else the site (FORGE_SITE_URL or kovati.dev).</summary>
    public static string MediaBase => SiteAddress.Normalise(Environment.GetEnvironmentVariable("FORGE_MEDIA_URL")) ?? SiteAddress.Base;

    /// <summary>
    /// A path the catalogue may name: one leading slash, then plain segments, an optional ?query, and a picture's
    /// extension. No scheme, no host, no backslash, no "..", no encoded dots or slashes, nothing below a space.
    /// </summary>
    public static bool IsSafeSitePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512) return false;
        if (path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal)) return false;
        if (path.Any(ch => ch < 0x20 || ch == '\\' || ch == 0x7f)) return false;
        if (path.Contains("://", StringComparison.Ordinal) || path.Contains('#')) return false;
        if (path.Contains('%')) return false; // the site writes plain paths; an encoded one is not ours
        var q = path.IndexOf('?');
        var plain = q >= 0 ? path[..q] : path;
        var segments = plain.Split('/');
        if (segments.Skip(1).Any(s => s.Length == 0 || s == "." || s == "..")) return false;
        return Extensions.Contains(Path.GetExtension(plain).ToLowerInvariant());
    }

    /// <summary>The cache file name of a site path: a hash of the whole path, ?v= included, and its extension.</summary>
    public static string KeyOf(string sitePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sitePath))).ToLowerInvariant()[..32];
        var ext = Path.GetExtension(Catalogue.LocalMediaPath(sitePath)).ToLowerInvariant();
        return hash + (Extensions.Contains(ext) ? ext : ".img");
    }

    /// <summary>The address a safe path is fetched from, on the site's host only; null for anything else.</summary>
    public Uri? AddressOf(string sitePath)
    {
        if (!IsSafeSitePath(sitePath)) return null;
        if (!Uri.TryCreate(SiteBase, sitePath, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.Equals(u.Host, SiteBase.Host, StringComparison.OrdinalIgnoreCase) || u.Port != SiteBase.Port) return null;
        return u;
    }

    /// <summary>The cached file for a path, or null. A hit counts as a use, for the eviction order.</summary>
    public string? Cached(string? sitePath)
    {
        if (!IsSafeSitePath(sitePath)) return null;
        var file = Path.Combine(Dir, KeyOf(sitePath!));
        if (!File.Exists(file)) return null;
        try { File.SetLastAccessTimeUtc(file, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return file;
    }

    /// <summary>The cached file, fetching it first when needed. Null when it cannot be had.</summary>
    public Task<string?> GetAsync(string? sitePath, CancellationToken ct = default)
    {
        if (Cached(sitePath) is { } hit) return Task.FromResult<string?>(hit);
        if (AddressOf(sitePath ?? "") is not { } address) return Task.FromResult<string?>(null);
        if (_failed.TryGetValue(sitePath!, out var when) && DateTime.UtcNow - when < RetryAfter) return Task.FromResult<string?>(null);
        return _inFlight.GetOrAdd(sitePath!, p => DownloadAsync(p, address, ct).ContinueWith(t =>
        {
            _inFlight.TryRemove(p, out _);
            var file = t.IsCompletedSuccessfully ? t.Result : null;
            if (file is null) _failed[p] = DateTime.UtcNow;
            return file;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
    }

    private async Task<string?> DownloadAsync(string sitePath, Uri address, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string? tmp = null;
        try
        {
            using var resp = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.OK) return null;
            // A redirect may not take the request off the site.
            if (resp.RequestMessage?.RequestUri is { } final && !string.Equals(final.Host, SiteBase.Host, StringComparison.OrdinalIgnoreCase)) return null;
            var type = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (type is null || !ImageTypes.Contains(type)) return null;
            if (resp.Content.Headers.ContentLength is > MaxFileBytes) return null;

            Directory.CreateDirectory(Dir);
            var file = Path.Combine(Dir, KeyOf(sitePath));
            tmp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
            long total = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > MaxFileBytes) return null;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            if (total == 0) return null;
            File.Move(tmp, file, overwrite: true);
            tmp = null;
            Evict();
            return file;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (tmp is not null) try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _gate.Release();
        }
    }

    /// <summary>Remove the files used longest ago until the folder is under its limit; left-over partial files go first.</summary>
    public void Evict()
    {
        lock (_evictLock)
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                var files = new DirectoryInfo(Dir).GetFiles();
                foreach (var part in files.Where(f => f.Extension == ".part" && DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromHours(1)))
                    try { part.Delete(); } catch (IOException) { }
                var kept = files.Where(f => f.Extension != ".part").OrderBy(f => f.LastAccessTimeUtc).ToList();
                var size = kept.Sum(f => f.Length);
                foreach (var f in kept)
                {
                    if (size <= MaxTotalBytes) break;
                    var length = f.Length; // before the delete: a deleted FileInfo has no length
                    try { f.Delete(); size -= length; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
