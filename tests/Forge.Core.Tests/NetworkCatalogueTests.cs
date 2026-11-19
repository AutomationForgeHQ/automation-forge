using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Forge.Core.Catalogue;
using Xunit;

namespace Forge.Core.Tests;

/// <summary>A handler that answers from a function and remembers what it was asked.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var resp = answer(request);
        resp.RequestMessage ??= request;
        return Task.FromResult(resp);
    }

    public static HttpResponseMessage Json(string body, string? etag = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null) r.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return r;
    }

    public static HttpResponseMessage Bytes(byte[] body, string type)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        r.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return r;
    }
}

/// <summary>Slice 5: the catalogue from the website, through the cache, under the loader's rule.</summary>
public class CatalogueClientTests : IDisposable
{
    private const string Url = "https://kovati.dev/hub/catalogue.json";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("forge-catnet-");
    private readonly string? _before = Environment.GetEnvironmentVariable("FORGE_DATA_DIR");

    public CatalogueClientTests() => Environment.SetEnvironmentVariable("FORGE_DATA_DIR", _dir.FullName);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FORGE_DATA_DIR", _before);
        _dir.Delete(true);
    }

    private static string Doc(string generatedAt, int schema = 1, string slug = "newkit", string name = "New Kit") =>
        $$"""
        { "schemaVersion": {{schema}}, "generatedAt": "{{generatedAt}}",
          "tracks": [ { "slug": "worldbuilding", "name": "Worldbuilding", "order": 3,
                        "capabilities": [ { "id": "kit", "name": "A kit", "product": "{{slug}}" } ] } ],
          "products": [ { "slug": "{{slug}}", "name": "{{name}}", "type": "Kit", "track": "worldbuilding", "line": "A new kit.",
                          "plugins": ["NewKit"], "media": { "card": "/media/{{slug}}/card.jpg?v=1" } } ] }
        """;

    private string CachePath => Path.Combine(_dir.FullName, "catalogue.json");

    [Fact]
    public async Task A_newer_copy_from_the_site_is_cached_and_wins_over_the_bundled_one()
    {
        var stub = new StubHandler(_ => StubHandler.Json(Doc("2099-01-01T00:00:00Z"), "\"v1\""));
        var client = new CatalogueClient(new HttpClient(stub), Url);
        Assert.Equal(CatalogueFetch.Updated, await client.FetchAsync());

        var loaded = CatalogueLoader.Load()!;
        Assert.Equal("cache", loaded.Source);
        // A product only the fetched copy has is there, with its track and its picture path as the site wrote it.
        Assert.Equal("New Kit", loaded.Product("newkit")!.Name);
        Assert.Equal("newkit", loaded.Track("worldbuilding")!.Capabilities.Single().Product);
        Assert.Equal("\"v1\"", File.ReadAllText(Path.Combine(_dir.FullName, "catalogue.etag")));
        Assert.Empty(Directory.GetFiles(_dir.FullName, "*.tmp"));
    }

    [Fact]
    public async Task The_etag_is_sent_back_and_a_304_changes_nothing()
    {
        var calls = 0;
        var stub = new StubHandler(req =>
        {
            calls++;
            if (req.Headers.TryGetValues("If-None-Match", out var v) && v.Single() == "\"v1\"")
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            return StubHandler.Json(Doc("2099-01-01T00:00:00Z"), "\"v1\"");
        });
        var client = new CatalogueClient(new HttpClient(stub), Url);
        Assert.Equal(CatalogueFetch.Updated, await client.FetchAsync());
        var before = File.GetLastWriteTimeUtc(CachePath);
        Assert.Equal(CatalogueFetch.NotModified, await client.FetchAsync());
        Assert.Equal(2, calls);
        Assert.False(stub.Requests[0].Headers.Contains("If-None-Match"));
        Assert.Equal("\"v1\"", stub.Requests[1].Headers.GetValues("If-None-Match").Single());
        Assert.Equal(before, File.GetLastWriteTimeUtc(CachePath));
    }

    [Fact]
    public async Task No_etag_is_sent_without_a_cached_copy()
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "catalogue.etag"), "\"stale\"");
        var stub = new StubHandler(_ => StubHandler.Json(Doc("2099-01-01T00:00:00Z")));
        Assert.Equal(CatalogueFetch.Updated, await new CatalogueClient(new HttpClient(stub), Url).FetchAsync());
        Assert.False(stub.Requests[0].Headers.Contains("If-None-Match"));
    }

    [Theory]
    [InlineData(2)]   // a newer schema: this build does not half-read it
    [InlineData(0)]
    public async Task A_schema_this_build_does_not_read_is_ignored_and_the_good_copy_stays(int schema)
    {
        File.WriteAllText(CachePath, Doc("2098-01-01T00:00:00Z", name: "Kept"));
        var stub = new StubHandler(_ => StubHandler.Json(Doc("2099-01-01T00:00:00Z", schema: schema, name: "Future"), "\"v2\""));
        var client = new CatalogueClient(new HttpClient(stub), Url);
        Assert.Equal(CatalogueFetch.Ignored, await client.FetchAsync());
        Assert.NotNull(client.LastMessage);
        Assert.Equal("Kept", CatalogueLoader.Load()!.Product("newkit")!.Name);
        Assert.False(File.Exists(Path.Combine(_dir.FullName, "catalogue.etag")));
    }

    [Fact]
    public async Task Broken_json_and_an_empty_catalogue_are_ignored()
    {
        File.WriteAllText(CachePath, Doc("2098-01-01T00:00:00Z", name: "Kept"));
        foreach (var body in new[] { "{ not json", """{ "schemaVersion": 1, "generatedAt": "2099-01-01T00:00:00Z", "tracks": [], "products": [] }""" })
        {
            var client = new CatalogueClient(new HttpClient(new StubHandler(_ => StubHandler.Json(body))), Url);
            Assert.Equal(CatalogueFetch.Ignored, await client.FetchAsync());
        }
        Assert.Equal("Kept", CatalogueLoader.Load()!.Product("newkit")!.Name);
    }

    [Fact]
    public async Task Offline_or_an_error_status_keeps_what_the_hub_has()
    {
        File.WriteAllText(CachePath, Doc("2099-01-01T00:00:00Z", name: "Kept"));
        var offline = new CatalogueClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("no network"))), Url);
        Assert.Equal(CatalogueFetch.Failed, await offline.FetchAsync());
        var down = new CatalogueClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))), Url);
        Assert.Equal(CatalogueFetch.Failed, await down.FetchAsync());
        Assert.Equal("Kept", CatalogueLoader.Load()!.Product("newkit")!.Name);

        // With no cache at all, the installer's copy is what remains.
        File.Delete(CachePath);
        Assert.Equal(CatalogueFetch.Failed, await offline.FetchAsync());
        var fallback = CatalogueLoader.Load();
        if (File.Exists(CatalogueLoader.BundledPath)) Assert.Equal("bundled", fallback!.Source);
        else Assert.Null(fallback);
    }

    [Fact]
    public async Task An_older_site_copy_is_cached_but_the_newer_bundled_copy_still_wins()
    {
        var stub = new StubHandler(_ => StubHandler.Json(Doc("2000-01-01T00:00:00Z")));
        Assert.Equal(CatalogueFetch.Updated, await new CatalogueClient(new HttpClient(stub), Url).FetchAsync());
        if (File.Exists(CatalogueLoader.BundledPath)) Assert.Equal("bundled", CatalogueLoader.Load()!.Source);
    }

    [Theory]
    [InlineData("2026-11-19T20:29:14Z", "2026-11-19T00:00:00Z", true)]
    [InlineData("2026-11-19T00:00:00Z", "2026-11-19T20:29:14Z", false)]
    [InlineData("2026-11-19T21:00:00+01:00", "2026-11-19T20:30:00Z", false)] // 20:00Z is earlier
    [InlineData("2026-11-19T20:29:14Z", "2026-11-19T20:29:14Z", false)]
    public void Generated_at_compares_as_time(string a, string b, bool newer) => Assert.Equal(newer, CatalogueLoader.IsNewer(a, b));

    [Fact]
    public void The_site_address_comes_from_the_environment_only_when_it_is_http()
    {
        var before = Environment.GetEnvironmentVariable("FORGE_SITE_URL");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_SITE_URL", "http://localhost:3002/");
            Assert.Equal("http://localhost:3002", SiteAddress.Base);
            Assert.Equal("http://localhost:3002/hub/catalogue.json", SiteAddress.CatalogueUrl);
            Environment.SetEnvironmentVariable("FORGE_SITE_URL", "file:///C:/evil");
            Assert.Equal("https://kovati.dev", SiteAddress.Base);
            Environment.SetEnvironmentVariable("FORGE_SITE_URL", null);
            Assert.Equal("https://kovati.dev/hub/catalogue.json", SiteAddress.CatalogueUrl);
        }
        finally { Environment.SetEnvironmentVariable("FORGE_SITE_URL", before); }
    }
}

/// <summary>Slice 5: pictures from the site into the disk cache, safely.</summary>
public class MediaCacheTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("forge-media-");
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    public void Dispose() => _dir.Delete(true);

    private MediaCache Cache(StubHandler stub, long max = MediaCache.DefaultMaxTotalBytes) =>
        new(new HttpClient(stub), "http://localhost:3002", _dir.FullName, max);

    [Fact]
    public void The_key_is_the_whole_path_with_its_stamp()
    {
        var a = MediaCache.KeyOf("/media/skinforge/card.jpg?v=20261111a");
        var b = MediaCache.KeyOf("/media/skinforge/card.jpg?v=20261112");
        var c = MediaCache.KeyOf("/media/skinforge/card.jpg");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a, MediaCache.KeyOf("/media/skinforge/card.jpg?v=20261111a"));
        Assert.EndsWith(".jpg", a);
        Assert.Matches("^[0-9a-f]{32}\\.jpg$", a);
        Assert.EndsWith(".webp", MediaCache.KeyOf("/media/hub/motionforge/loop.webp"));
    }

    [Theory]
    [InlineData("/media/motionforge/card.jpg", true)]
    [InlineData("/media/skinforge/v4_hero_card.jpg?v=20261111a", true)]
    [InlineData("/media/hub/animation/door.webp", true)]
    [InlineData("media/motionforge/card.jpg", false)]          // not a site path
    [InlineData("//evil.example/card.jpg", false)]             // another host
    [InlineData("https://evil.example/card.jpg", false)]
    [InlineData("/media/../../secrets/card.jpg", false)]
    [InlineData("/media/%2e%2e/card.jpg", false)]
    [InlineData("/media\\..\\card.jpg", false)]
    [InlineData("/media/motionforge/card.mp4", false)]          // films are not stills
    [InlineData("/media/motionforge/notes.html", false)]
    [InlineData("/media//card.jpg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_plain_picture_paths_on_the_site_are_asked_for(string? path, bool safe) => Assert.Equal(safe, MediaCache.IsSafeSitePath(path));

    [Fact]
    public void An_address_is_always_on_the_sites_own_host()
    {
        var cache = Cache(new StubHandler(_ => throw new InvalidOperationException()));
        Assert.Equal("http://localhost:3002/media/a/card.jpg?v=1", cache.AddressOf("/media/a/card.jpg?v=1")!.ToString());
        Assert.Null(cache.AddressOf("//evil.example/a.jpg"));
        Assert.Null(cache.AddressOf("https://evil.example/a.jpg"));
    }

    [Fact]
    public async Task A_picture_is_downloaded_once_and_then_served_from_disk()
    {
        var stub = new StubHandler(_ => StubHandler.Bytes(Jpeg, "image/jpeg"));
        var cache = Cache(stub);
        var path = "/media/newkit/card.jpg?v=1";
        Assert.Null(cache.Cached(path));
        var tasks = Enumerable.Range(0, 5).Select(_ => cache.GetAsync(path)).ToList();
        var files = await Task.WhenAll(tasks);
        Assert.All(files, f => Assert.Equal(files[0], f));
        Assert.Single(stub.Requests);                                 // five controls, one download
        Assert.Equal(Jpeg, File.ReadAllBytes(files[0]!));
        Assert.Equal(files[0], cache.Cached(path));
        Assert.Equal(Path.Combine(_dir.FullName, MediaCache.KeyOf(path)), files[0]);
        Assert.Empty(Directory.GetFiles(_dir.FullName, "*.part"));
        Assert.Equal(files[0], await cache.GetAsync(path));
        Assert.Single(stub.Requests);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    [InlineData("image/svg+xml")]
    public async Task Only_an_image_answer_is_kept(string type)
    {
        var cache = Cache(new StubHandler(_ => StubHandler.Bytes(Jpeg, type)));
        Assert.Null(await cache.GetAsync("/media/x/card.jpg"));
        Assert.Empty(_dir.GetFiles());
    }

    [Fact]
    public async Task A_file_over_the_cap_is_refused_and_leaves_nothing()
    {
        var big = new byte[MediaCache.MaxFileBytes + 1];
        var cache = Cache(new StubHandler(_ => StubHandler.Bytes(big, "image/jpeg")));
        Assert.Null(await cache.GetAsync("/media/x/huge.jpg"));
        Assert.Empty(_dir.GetFiles());
    }

    [Fact]
    public async Task A_failure_is_not_asked_again_at_once_and_offline_is_quiet()
    {
        var stub = new StubHandler(_ => throw new HttpRequestException("offline"));
        var cache = Cache(stub);
        Assert.Null(await cache.GetAsync("/media/x/card.jpg"));
        Assert.Null(await cache.GetAsync("/media/x/card.jpg"));
        Assert.Single(stub.Requests);
        var missing = Cache(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await missing.GetAsync("/media/x/gone.jpg"));
    }

    [Fact]
    public async Task An_unsafe_path_never_reaches_the_network()
    {
        var stub = new StubHandler(_ => StubHandler.Bytes(Jpeg, "image/jpeg"));
        var cache = Cache(stub);
        Assert.Null(await cache.GetAsync("https://evil.example/a.jpg"));
        Assert.Null(await cache.GetAsync("/media/../a.jpg"));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void Eviction_removes_the_files_used_longest_ago_first()
    {
        var cache = Cache(new StubHandler(_ => throw new InvalidOperationException()), max: 2500);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 4; i++)
        {
            var f = Path.Combine(_dir.FullName, $"{i}.jpg");
            File.WriteAllBytes(f, new byte[1000]);
            File.SetLastAccessTimeUtc(f, now.AddMinutes(i - 10)); // 0 is the oldest
        }
        cache.Evict();
        Assert.Equal(["2.jpg", "3.jpg"], _dir.GetFiles().Select(f => f.Name).OrderBy(n => n));
    }
}
