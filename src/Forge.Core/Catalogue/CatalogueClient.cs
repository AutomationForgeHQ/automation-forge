using System.Net;

namespace Forge.Core.Catalogue;

public enum CatalogueFetch
{
    /// <summary>A new, understood copy was written to the cache.</summary>
    Updated,
    /// <summary>The site said the cached copy is current (304), or sent the same file again.</summary>
    NotModified,
    /// <summary>The site sent something this build does not use: a newer schema, or a file that does not read.</summary>
    Ignored,
    /// <summary>No answer: offline, a timeout, an error status. What the Hub has stays.</summary>
    Failed,
}

/// <summary>
/// Fetches catalogue.json from the website (12.5): at start and with every update check, anonymous, with the
/// ETag of the cached copy so an unchanged file costs a 304. A copy is written to the cache only when it reads
/// whole and its schema is one this build understands, and it is written atomically (a temporary file, then a
/// move), so a cut connection or a newer schema never replaces a good copy. Which copy is used is still
/// <see cref="CatalogueLoader.Load"/>'s rule: the newest generatedAt among the cache and the installer's copy.
/// Offline or failing, nothing changes and nothing is said.
/// </summary>
public sealed class CatalogueClient
{
    /// <summary>A catalogue is about 130 KB; anything far larger is not one.</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly string _etagPath;

    public string Url { get; }

    /// <summary>Why the last fetch did not update, for the Details log. Null after a success.</summary>
    public string? LastMessage { get; private set; }

    public CatalogueClient(HttpClient http, string? url = null, string? cacheDir = null)
    {
        _http = http;
        Url = url ?? SiteAddress.CatalogueUrl;
        var dir = cacheDir ?? Paths.DataDir;
        _cachePath = Path.Combine(dir, "catalogue.json");
        _etagPath = Path.Combine(dir, "catalogue.etag");
    }

    public async Task<CatalogueFetch> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url);
            req.Headers.Accept.ParseAdd("application/json");
            if (File.Exists(_cachePath) && File.Exists(_etagPath))
                req.Headers.TryAddWithoutValidation("If-None-Match", (await File.ReadAllTextAsync(_etagPath, ct)).Trim());

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == HttpStatusCode.NotModified) { LastMessage = null; return CatalogueFetch.NotModified; }
            if (!resp.IsSuccessStatusCode) return Fail($"{Url} answered {(int)resp.StatusCode}.");
            if (resp.Content.Headers.ContentLength > MaxBytes) return Fail($"{Url} is larger than a catalogue can be.", CatalogueFetch.Ignored);

            var text = await ReadCappedAsync(resp.Content, ct);
            if (text is null) return Fail($"{Url} is larger than a catalogue can be.", CatalogueFetch.Ignored);

            Catalogue parsed;
            try { parsed = Catalogue.Parse(text, Url); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
            {
                return Fail($"{Url} does not read as a catalogue: {ex.Message}", CatalogueFetch.Ignored);
            }
            if (parsed.SchemaVersion is < 1 or > Catalogue.SupportedSchema)
                return Fail($"{Url} has schema {parsed.SchemaVersion}; this Hub reads up to {Catalogue.SupportedSchema}. Kept the copy it has.", CatalogueFetch.Ignored);
            if (parsed.Tracks.Count == 0 || parsed.Products.Count == 0)
                return Fail($"{Url} has no tracks or no products. Kept the copy it has.", CatalogueFetch.Ignored);

            var same = File.Exists(_cachePath) && await File.ReadAllTextAsync(_cachePath, ct) == text;
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            if (!same) WriteAtomically(_cachePath, text);
            if (resp.Headers.ETag is { } etag) WriteAtomically(_etagPath, etag.ToString());
            else if (File.Exists(_etagPath)) File.Delete(_etagPath);
            LastMessage = null;
            return same ? CatalogueFetch.NotModified : CatalogueFetch.Updated;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return Fail($"Could not fetch {Url}: {ex.Message}");
        }
    }

    private CatalogueFetch Fail(string message, CatalogueFetch result = CatalogueFetch.Failed)
    {
        LastMessage = message;
        return result;
    }

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxBytes) return null;
            buffer.Write(chunk, 0, n);
        }
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>Write beside the target, then move over it: a reader sees the old file or the new one, never half of one.</summary>
    public static void WriteAtomically(string path, string text)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
