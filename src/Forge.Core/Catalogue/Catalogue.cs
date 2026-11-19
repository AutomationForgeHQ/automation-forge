using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core.Catalogue;

/// <summary>
/// catalogue.json: the tracks, the capabilities, the products, their words and pictures. Generated in the
/// website repository from the site's own data; the Hub never types a product name, a line or a label itself.
/// The manifest stays the document for components, versions, sizes, dependencies and notes.
///
/// Read with unknown fields ignored, so the site can add fields without a Hub release. A file whose
/// <see cref="SchemaVersion"/> is above <see cref="SupportedSchema"/> is not used at all.
/// </summary>
public sealed class Catalogue
{
    /// <summary>The newest schema this build understands.</summary>
    public const int SupportedSchema = 1;

    public int SchemaVersion { get; set; }
    public string GeneratedAt { get; set; } = "";
    public string MediaBase { get; set; } = "";
    public CatalogueLinks Links { get; set; } = new();
    public CatalogueLabels Labels { get; set; } = new();
    public List<AlwaysEntry> Always { get; set; } = [];
    public List<string> HeroOrder { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    public List<Track> Tracks { get; set; } = [];
    public List<Product> Products { get; set; } = [];

    /// <summary>Where this copy came from: "bundled", "cache", or a path or address. Not part of the file.</summary>
    [JsonIgnore] public string Source { get; set; } = "";

    public Track? Track(string? slug) =>
        Tracks.FirstOrDefault(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public Product? Product(string? slug) =>
        Products.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<Track> TracksInOrder => Tracks.OrderBy(t => t.Order);

    /// <summary>
    /// The product a component belongs to: the first product that installs it, else the first that offers it
    /// as agent access. If two products claim one component the first in the file owns it (13.2).
    /// </summary>
    public Product? ProductOfComponent(string componentId) =>
        Products.FirstOrDefault(p => p.Plugins.Contains(componentId, StringComparer.OrdinalIgnoreCase))
        ?? Products.FirstOrDefault(p => p.AgentPlugins.Contains(componentId, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// An engine's short name in a sentence ("Kimodo engine", "Kimodo: about 20 GB…"): its name without the
    /// name of the tool it extends, unless what is left is a generic word, then its whole name.
    /// </summary>
    public string ShortName(Product p)
    {
        if (Product(p.Extends) is { } parent && p.Name.StartsWith(parent.Name + " ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = p.Name[(parent.Name.Length + 1)..];
            return rest is "Local" or "Cloud" ? p.Name : rest;
        }
        return p.Name;
    }

    /// <summary>The words for a price key ("free", "free-tier", "paid"), from the site's own labels.</summary>
    public string PriceLabel(string? price) =>
        price is not null && Labels.Price.TryGetValue(price, out var s) ? s
        : price switch { "free" => "Free", "free-tier" => "Free tier", "paid" => "Paid", _ => "" };

    public string StageLabel(Stage stage) =>
        Labels.Stage.TryGetValue(stage.Key(), out var s) ? s : stage.DefaultLabel();

    public string LabsLine => string.IsNullOrWhiteSpace(Labels.LabsLine)
        ? "Early experiments: they work for us, may break, and get no support yet."
        : Labels.LabsLine;

    /// <summary>A media path, as the site writes it, without the ?v= stamp: the key into the installer's set.</summary>
    public static string LocalMediaPath(string path)
    {
        var q = path.IndexOf('?');
        return (q >= 0 ? path[..q] : path).TrimStart('/');
    }

    public static Catalogue Parse(string json, string source)
    {
        var c = JsonSerializer.Deserialize<Catalogue>(json, Json) ?? throw new InvalidDataException("The catalogue is empty.");
        c.Source = source;
        return c;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed class CatalogueLinks
{
    public string Site { get; set; } = "https://kovati.dev";
    public string Account { get; set; } = "https://app.kovati.dev";
    public string Docs { get; set; } = "https://docs.kovati.dev";
    public string Discord { get; set; } = "https://kovati.dev/discord";
    /// <summary>The account app's cart, with {ids} for the store ids, comma separated.</summary>
    public string Cart { get; set; } = "";
    public string Buy { get; set; } = "";
    /// <summary>What the account owns; slice 1 opens this with #storeId as the "Add to cart" hand-off (4.6).</summary>
    public string Owned { get; set; } = "https://app.kovati.dev/plugins/";
    public string UnrealEngine { get; set; } = "https://www.unrealengine.com/download";
    public string ReleaseNotes { get; set; } = "";
    public string GettingStarted { get; set; } = "";
    public string KeysHelp { get; set; } = "";
    public string Troubleshooting { get; set; } = "";
}

public sealed class CatalogueLabels
{
    public Dictionary<string, string> Price { get; set; } = [];
    public string Trial { get; set; } = "Free trial";
    public Dictionary<string, string> Stage { get; set; } = [];
    public Dictionary<string, string> AddonKind { get; set; } = [];
    public string LabsLine { get; set; } = "";
}

/// <summary>A component every engine install gets: the editor plugin.</summary>
public sealed class AlwaysEntry
{
    public string Plugin { get; set; } = "";
    public string Name { get; set; } = "";
    public string Line { get; set; } = "";
}

public sealed class Track
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public string Subtitle { get; set; } = "";
    public string Eyebrow { get; set; } = "";
    public string Promise { get; set; } = "";
    public string Lede { get; set; } = "";
    public string Site { get; set; } = "";
    public TrackMedia Media { get; set; } = new();
    public Film? Film { get; set; }
    public List<string> WorksWith { get; set; } = [];
    public List<NameLine> Needs { get; set; } = [];
    public List<Capability> Capabilities { get; set; } = [];
    public NextTrack? Next { get; set; }

    public Capability? Capability(string? id) =>
        Capabilities.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class TrackMedia
{
    public string? Door { get; set; }
    public string? DoorVideo { get; set; }
    public string? Hero { get; set; }
    public string? HeroVideo { get; set; }
    public string? Credit { get; set; }
    public string? DoorLoop { get; set; }
    public string? HeroLoop { get; set; }
}

public sealed class Film
{
    public string? Src { get; set; }
    public string? Youtube { get; set; }
    public string? Poster { get; set; }
    public string? Title { get; set; }
    public string? Line { get; set; }
}

public sealed class NameLine
{
    public string Name { get; set; } = "";
    public string Line { get; set; } = "";
}

public sealed class NextTrack
{
    public string Track { get; set; } = "";
    public string Line { get; set; } = "";
}

public sealed class Capability
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Line { get; set; } = "";
    public string? Title { get; set; }
    public string Product { get; set; } = "";
    public List<EngineRef> Engines { get; set; } = [];
    public string? EngineLabel { get; set; }
    public bool EngineRequired { get; set; }
    public CapabilityMedia Media { get; set; } = new();
}

public sealed class EngineRef
{
    public string Product { get; set; } = "";
    public bool Default { get; set; }
    public bool Labs { get; set; }
}

public sealed class CapabilityMedia
{
    public string? Poster { get; set; }
    public string? Video { get; set; }
    public string? Loop { get; set; }
}

public sealed class Product
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Tool, Add-on, Utility, Kit, Playbook, App or Plan.</summary>
    public string Type { get; set; } = "";
    public string Track { get; set; } = "";
    public List<string> AlsoIn { get; set; } = [];
    public bool Hero { get; set; }
    public string? Tagline { get; set; }
    public string Line { get; set; } = "";
    /// <summary>free, free-tier or paid.</summary>
    public string Price { get; set; } = "free";
    public bool Trial { get; set; }
    /// <summary>Only on free-tier products: false until the free tier can really be installed (6.6).</summary>
    public bool? FreeTierLive { get; set; }
    public string? StoreId { get; set; }
    /// <summary>The component ids it installs. The first one decides the stage and the version shown.</summary>
    public List<string> Plugins { get; set; } = [];
    public List<string> AgentPlugins { get; set; } = [];
    public string? BelongsTo { get; set; }
    public string? Extends { get; set; }
    public string? AddonKind { get; set; }
    public string? Runs { get; set; }
    public string? Vendor { get; set; }
    public List<ProductNeed> Needs { get; set; } = [];
    public List<string> Runners { get; set; } = [];
    public string? Later { get; set; }
    public List<Edition> Editions { get; set; } = [];
    public List<string> Contents { get; set; } = [];
    /// <summary>The apps a playbook runs through. The site writes one name or a list.</summary>
    [JsonConverter(typeof(StringOrListConverter))] public List<string> Software { get; set; } = [];
    public List<NameLine> Characters { get; set; } = [];
    public string? Mark { get; set; }
    public ProductMedia Media { get; set; } = new();
    public ProductPage? Page { get; set; }
    public string? Site { get; set; }
    public string? Docs { get; set; }
    public string? Changelog { get; set; }
    public bool? Listed { get; set; }

    [JsonIgnore] public bool IsListed => Listed != false;
    [JsonIgnore] public string? MainComponent => Plugins.FirstOrDefault();
    [JsonIgnore] public bool IsEngine => AddonKind == "provider";

    /// <summary>The id the account sells it under; the first component when the catalogue names none.</summary>
    [JsonIgnore] public string? StoreKey => StoreId ?? MainComponent;


}

public sealed class ProductNeed
{
    /// <summary>key, app, gpu, account, device.</summary>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Line { get; set; } = "";
    public string? KeyId { get; set; }
    public bool Optional { get; set; }
}

public sealed class Edition
{
    public string Name { get; set; } = "";
    public string Price { get; set; } = "";
    public string Line { get; set; } = "";
}

public sealed class ProductMedia
{
    public string? Card { get; set; }
    public string? CardVideo { get; set; }
    public string? Focus { get; set; }
    public string? Hero { get; set; }
    public string? HeroVideo { get; set; }
    public string? HeroFocus { get; set; }
    public string? CardLoop { get; set; }
}

public sealed class ProductPage
{
    public string? Promise { get; set; }
    public string? Subline { get; set; }
    public string? PriceLine { get; set; }
    public List<TitleLine> Adds { get; set; } = [];
    public PageInside? Inside { get; set; }
    public List<NameLine> Takes { get; set; } = [];
    public PageShots? Shots { get; set; }
    public Film? Film { get; set; }
}

public sealed class TitleLine
{
    public string Title { get; set; } = "";
    public string Line { get; set; } = "";
}

public sealed class PageInside
{
    public string? Title { get; set; }
    public string? Line { get; set; }
    public List<NameLine> Items { get; set; } = [];
}

public sealed class PageShots
{
    public string? Kind { get; set; }
    public string? Title { get; set; }
    public string? Line { get; set; }
    public List<Shot> Items { get; set; } = [];
}

public sealed class Shot
{
    public string Src { get; set; } = "";
    public string? Video { get; set; }
    public string? Label { get; set; }
    public string? Caption { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>A field the site writes as one string or as a list of them.</summary>
public sealed class StringOrListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return [reader.GetString() ?? ""];
        if (reader.TokenType == JsonTokenType.Null) return [];
        return JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? [];
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}