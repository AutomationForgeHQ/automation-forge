using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core.Cloud;

/// <summary>
/// What the account service says about selling: the price amount, whether a product is for sale, and
/// whether checkout is live (`payments`). GET {api}/catalogue, no sign-in needed, kept for a day in the data
/// folder. Older than a day, or never read: prices are not shown and buying counts as not live.
/// </summary>
public sealed class AccountCatalogue
{
    /// <summary>
    /// The one switch for "Add to cart" (open question 4): buying is live only when the account API says
    /// `payments: stripe`. Change this word if the live mode is ever named differently.
    /// </summary>
    public const string LivePayments = "stripe";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [JsonPropertyName("payments")] public string? Payments { get; set; }
    [JsonPropertyName("plugins")] public List<AccountPlugin> Plugins { get; set; } = [];
    [JsonIgnore] public DateTimeOffset ReadAt { get; set; }

    public bool PaymentsLive => string.Equals(Payments, LivePayments, StringComparison.OrdinalIgnoreCase);

    public AccountPlugin? Plugin(string? id) =>
        id is null ? null : Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>"$49.99", or null when the account gives no amount.</summary>
    public string? PriceOf(string? id) => Plugin(id)?.Price is { Amount: > 0 } p ? Price(p.Amount) : null;

    private static string Price(long cents) => cents % 100 == 0 ? $"${cents / 100}" : $"${cents / 100}.{cents % 100:00}";

    private static string CachePath => Path.Combine(Paths.DataDir, "account-catalogue.json");

    /// <summary>Read an answer of GET {api}/catalogue. Fields this build does not know are ignored; absent ones keep their defaults.</summary>
    public static AccountCatalogue? Parse(string json) => JsonSerializer.Deserialize<AccountCatalogue>(json, Json);

    /// <summary>
    /// The cached answer when it is younger than a day; otherwise the network; otherwise null. <paramref name="force"/>
    /// (Check now) asks the network first and keeps the cached answer when the network fails.
    /// </summary>
    public static async Task<AccountCatalogue?> GetAsync(HttpClient http, bool offline = false, CancellationToken ct = default, bool force = false)
    {
        var cached = ReadCache();
        if (!force && cached is not null && DateTimeOffset.UtcNow - cached.ReadAt < TimeSpan.FromDays(1)) return cached;
        if (offline) return force ? cached : null;
        try
        {
            var text = await http.GetStringAsync($"{CloudConfig.ApiUrl}/catalogue", ct);
            var fresh = Parse(text);
            if (fresh is null) return force ? cached : null;
            fresh.ReadAt = DateTimeOffset.UtcNow;
            // The beta Hub keeps the same file in the same folder: written whole, so neither ever reads half of it.
            try { Licence.LicenceStore.WriteAtomically(CachePath, System.Text.Encoding.UTF8.GetBytes(text)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // the answer stands; the next read asks again
            return fresh;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return force ? cached : null;
        }
    }

    private static AccountCatalogue? ReadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var c = JsonSerializer.Deserialize<AccountCatalogue>(File.ReadAllText(CachePath), Json);
            if (c is not null) c.ReadAt = File.GetLastWriteTimeUtc(CachePath);
            return c;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }
}

public sealed class AccountPlugin
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("price")] public AccountPrice? Price { get; set; }
    [JsonPropertyName("forSale")] public bool ForSale { get; set; }
    [JsonPropertyName("released")] public bool Released { get; set; }

    /// <summary>
    /// A paid plugin's free tier: "lite" or "trial", or null (free plugins, paid ones without one, and an API older than
    /// the field). Anyone signed in may download its free-tier builds; the licence decides what they unlock.
    /// </summary>
    [JsonPropertyName("tier")] public string? Tier { get; set; }
    /// <summary>The first version a free-tier download may have (x.y.z). Older builds need a licence.</summary>
    [JsonPropertyName("tierSince")] public string? TierSince { get; set; }
    /// <summary>The plugin this one comes with (bought with another), or null.</summary>
    [JsonPropertyName("includedWith")] public string? IncludedWith { get; set; }
    [JsonPropertyName("versions")] public List<AccountVersion> Versions { get; set; } = [];

    /// <summary>The free tier when it is one this build knows ("lite", "trial"); anything else reads as none, so a typo never opens a download.</summary>
    [JsonIgnore] public string? FreeTier => Tier is "lite" or "trial" ? Tier : null;

    /// <summary>
    /// Whether an account that does not hold the plugin by a licensed route may download this build: the offer's
    /// `freeTier` for the version when it lists one, else the version against `tierSince`. False without a free tier.
    /// </summary>
    public bool ServesFreeTier(Manifest.VersionInfo v)
    {
        if (FreeTier is null) return false;
        var listed = Versions.FirstOrDefault(x => x.Version == v.Version && string.Equals(x.Engine, v.Engine, StringComparison.OrdinalIgnoreCase)
                                                  && x.FreeTier is not null);
        if (listed is not null) return listed.FreeTier == true;
        return PlainVersion.AtLeast(v.Version, TierSince);
    }
}

public sealed class AccountVersion
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("engine")] public string Engine { get; set; } = "";
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    /// <summary>Null in an answer older than the field.</summary>
    [JsonPropertyName("freeTier")] public bool? FreeTier { get; set; }
}

/// <summary>Plain x.y.z versions, compared as the account API compares them (freetier.ts): anything else is never at or above a floor.</summary>
public static class PlainVersion
{
    private static readonly System.Text.RegularExpressions.Regex Plain = new(@"^(\d{1,6})\.(\d{1,6})\.(\d{1,6})$");

    public static (int, int, int)? Parse(string? v)
    {
        var m = v is null ? null : Plain.Match(v.Trim());
        return m is { Success: true } ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    /// <summary>version >= floor, both plain x.y.z; false when either is not.</summary>
    public static bool AtLeast(string? version, string? floor) =>
        Parse(version) is { } v && Parse(floor) is { } f && v.CompareTo(f) >= 0;
}

public sealed class AccountPrice
{
    [JsonPropertyName("amount")] public long Amount { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "usd";
}
