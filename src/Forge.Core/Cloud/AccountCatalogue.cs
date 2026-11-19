using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core.Cloud;

/// <summary>
/// What the account service says about selling (6.6): the price amount, whether a product is for sale, and
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
    public string? PriceOf(string? id) => Plugin(id)?.Price is { Amount: > 0 } p ? Catalogue.Words.Price(p.Amount) : null;

    private static string CachePath => Path.Combine(Paths.DataDir, "account-catalogue.json");

    /// <summary>The cached answer when it is younger than a day; otherwise the network; otherwise null.</summary>
    public static async Task<AccountCatalogue?> GetAsync(HttpClient http, bool offline = false, CancellationToken ct = default)
    {
        var cached = ReadCache();
        if (cached is not null && DateTimeOffset.UtcNow - cached.ReadAt < TimeSpan.FromDays(1)) return cached;
        if (offline) return null;
        try
        {
            var text = await http.GetStringAsync($"{CloudConfig.ApiUrl}/catalogue", ct);
            var fresh = JsonSerializer.Deserialize<AccountCatalogue>(text, Json);
            if (fresh is null) return null;
            fresh.ReadAt = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(Paths.DataDir);
            await File.WriteAllTextAsync(CachePath, text, ct);
            return fresh;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
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
    [JsonPropertyName("price")] public AccountPrice? Price { get; set; }
    [JsonPropertyName("forSale")] public bool ForSale { get; set; }
    [JsonPropertyName("released")] public bool Released { get; set; }
}

public sealed class AccountPrice
{
    [JsonPropertyName("amount")] public long Amount { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "usd";
}
