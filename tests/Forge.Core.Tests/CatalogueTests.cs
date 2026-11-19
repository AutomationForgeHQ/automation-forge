using Forge.Core.Catalogue;
using Xunit;

namespace Forge.Core.Tests;

public class CatalogueTests
{
    [Fact]
    public void The_shipped_catalogue_reads_whole()
    {
        var c = Fixtures.Catalogue();
        Assert.Equal(1, c.SchemaVersion);
        Assert.Equal(["characters", "animation", "worldbuilding", "storytelling"], c.TracksInOrder.Select(t => t.Slug));
        Assert.Equal(44, c.Products.Count);
        Assert.Equal(8, c.HeroOrder.Count);
        Assert.Equal(["AutomationForgeHub", "AutomationForge"], c.Always.Select(a => a.Plugin));
        Assert.Equal("Kovati Hub link", c.Always[0].Name);
        Assert.Equal(["AutomationForgeToolset"], c.Always[1].AgentPlugins);
        Assert.All(c.Always, a => Assert.False(string.IsNullOrWhiteSpace(a.Line)));
        Assert.Equal("Agent access for Kovati ledger", c.FoundationName("AutomationForgeToolset"));
        Assert.Equal("Early experiments: they work for us, may break, and get no support yet.", c.LabsLine);
        Assert.Contains("Marvelous Designer", c.Product("marvelous-designer-playbook")!.Software);
    }

    [Fact]
    public void Every_capability_and_engine_names_a_product_and_every_component_is_in_the_manifest()
    {
        var c = Fixtures.Catalogue();
        var m = Fixtures.Manifest();
        foreach (var t in c.Tracks)
        {
            Assert.NotEmpty(t.Capabilities);
            foreach (var cap in t.Capabilities)
            {
                Assert.NotNull(c.Product(cap.Product));
                foreach (var e in cap.Engines) Assert.NotNull(c.Product(e.Product));
            }
        }
        foreach (var id in c.Products.SelectMany(p => p.Plugins.Concat(p.AgentPlugins)))
            Assert.NotNull(m.Plugin(id));
    }

    [Fact]
    public void Every_still_it_names_is_shipped_beside_it()
    {
        var c = Fixtures.Catalogue();
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Forge.Hub", "catalogue"));
        var paths = c.Tracks.SelectMany(t => new[] { t.Media.Door, t.Media.Hero, t.Film?.Poster }.Concat(t.Capabilities.Select(x => x.Media.Poster)))
            .Concat(c.Products.SelectMany(p => new[] { p.Media.Card, p.Media.Hero, p.Page?.Film?.Poster }))
            .OfType<string>().Select(Catalogue.Catalogue.LocalMediaPath).Distinct().ToList();
        Assert.Equal(71, paths.Count);
        foreach (var p in paths) Assert.True(File.Exists(Path.Combine(root, p)), p);
    }

    [Fact]
    public void Unknown_fields_are_ignored_and_a_newer_schema_is_refused()
    {
        var dir = Directory.CreateTempSubdirectory("forge-cat-");
        try
        {
            var ok = Path.Combine(dir.FullName, "ok.json");
            File.WriteAllText(ok, """{ "schemaVersion": 1, "generatedAt": "2026-11-20T00:00:00Z", "somethingNew": { "x": 1 }, "tracks": [], "products": [ { "slug": "a", "name": "A", "brandNewField": true } ] }""");
            var read = CatalogueLoader.TryRead(ok, "test");
            Assert.NotNull(read);
            Assert.Equal("A", read!.Product("a")!.Name);

            var future = Path.Combine(dir.FullName, "future.json");
            File.WriteAllText(future, """{ "schemaVersion": 2, "generatedAt": "2027-01-01T00:00:00Z", "tracks": [], "products": [] }""");
            Assert.Null(CatalogueLoader.TryRead(future, "test"));

            var broken = Path.Combine(dir.FullName, "broken.json");
            File.WriteAllText(broken, "{ not json");
            Assert.Null(CatalogueLoader.TryRead(broken, "test"));
            Assert.NotNull(CatalogueLoader.LastError);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void The_newest_understood_copy_wins_and_an_explicit_file_wins_outright()
    {
        var dir = Directory.CreateTempSubdirectory("forge-data-");
        var before = Environment.GetEnvironmentVariable("FORGE_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_DATA_DIR", dir.FullName);
            // A cache newer than anything shipped wins over the bundled copy...
            File.WriteAllText(Path.Combine(dir.FullName, "catalogue.json"),
                """{ "schemaVersion": 1, "generatedAt": "2099-01-01T00:00:00Z", "tracks": [], "products": [] }""");
            Assert.Equal("cache", CatalogueLoader.Load()!.Source);
            // ...an older cache does not...
            File.WriteAllText(Path.Combine(dir.FullName, "catalogue.json"),
                """{ "schemaVersion": 1, "generatedAt": "2000-01-01T00:00:00Z", "tracks": [], "products": [] }""");
            var loaded = CatalogueLoader.Load();
            if (File.Exists(CatalogueLoader.BundledPath)) Assert.Equal("bundled", loaded!.Source);
            // ...and a named file is used as it is.
            Assert.Equal(Fixtures.CataloguePath, CatalogueLoader.Load(Fixtures.CataloguePath)!.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_DATA_DIR", before);
            dir.Delete(true);
        }
    }

    [Fact]
    public void Terrainforge_shows_paid_while_its_free_tier_is_not_live()
    {
        var c = Fixtures.Catalogue();
        var m = Fixtures.Manifest();
        var tf = c.Product("terrainforge")!;
        Assert.Equal("paid", ProductFacts.PriceKey(tf, m));
        Assert.True(ProductFacts.FreeTierPromised(tf, m));
        tf.FreeTierLive = true;
        Assert.Equal("free-tier", ProductFacts.PriceKey(tf, m));
        // SkinForge has no release: its free tier is still the promise the site makes.
        Assert.Equal("free-tier", ProductFacts.PriceKey(c.Product("skinforge")!, m));
    }
}

public class StageAndWordsTests
{
    [Theory]
    [InlineData("0.0.3", Stage.Labs)]
    [InlineData("0.1.2", Stage.Early)]
    [InlineData("0.2.0", Stage.Beta)]
    [InlineData("0.9.9", Stage.Beta)]
    [InlineData("1.0.0", Stage.Released)]
    [InlineData(null, Stage.Coming)]
    [InlineData("", Stage.Coming)]
    public void Stage_rule(string? version, Stage expected) => Assert.Equal(expected, Stages.Of(version));

    [Theory]
    [InlineData(400_000, "0.4 MB")]
    [InlineData(12_272_000, "12 MB")]
    [InlineData(2_361_000, "2.4 MB")]
    [InlineData(1_200_000_000, "1.2 GB")]
    [InlineData(20_000_000_000, "20 GB")]
    public void Sizes(long bytes, string text) => Assert.Equal(text, Words.Size(bytes));

    [Fact]
    public void Counts_and_prices()
    {
        Assert.Equal("1 capability", Words.Count(1, "capability", "capabilities"));
        Assert.Equal("3 updates", Words.Count(3, "update"));
        Assert.Equal("$49.99", Words.Price(4999));
        Assert.Equal("$69", Words.Price(6900));
    }
}
