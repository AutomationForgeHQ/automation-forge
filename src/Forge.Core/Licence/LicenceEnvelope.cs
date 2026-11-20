using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core.Licence;

/// <summary>
/// What `GET {api}/licence` answers, and what licence.json holds byte for byte:
/// <c>{ "format": "kovati-licence", "v": 1, "kid": "k1", "payload": "&lt;base64url of UTF-8 JSON&gt;", "sig": "&lt;base64url Ed25519&gt;" }</c>.
/// The signature is over the decoded payload bytes (the UTF-8 JSON), never over a re-serialised copy.
/// </summary>
public sealed record LicenceEnvelope(string Format, int V, string Kid, string Payload, string Sig)
{
    public const string FormatName = "kovati-licence";
    public const int Version = 1;

    /// <summary>The envelope from raw JSON text, or null with the reason. Nothing is verified here.</summary>
    public static LicenceEnvelope? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) { error = "not an object"; return null; }
            string? Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? Int(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
            var format = Str("format");
            var ver = Int("v");
            var kid = Str("kid");
            var payload = Str("payload");
            var sig = Str("sig");
            if (format != FormatName) { error = $"format is not {FormatName}"; return null; }
            if (ver != Version) { error = $"envelope version {ver?.ToString(CultureInfo.InvariantCulture) ?? "missing"} is not {Version}"; return null; }
            if (string.IsNullOrEmpty(kid) || string.IsNullOrEmpty(payload) || string.IsNullOrEmpty(sig)) { error = "kid, payload or sig missing"; return null; }
            return new LicenceEnvelope(format, ver.Value, kid, payload, sig);
        }
        catch (JsonException ex) { error = $"not JSON ({ex.Message})"; return null; }
    }
}

/// <summary>The signed part. Times are UTC instants; <see cref="Products"/> lists only paid products the account holds.</summary>
public sealed class LicencePayload
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("iss")] public string Iss { get; set; } = "";
    /// <summary>32 hex: <see cref="SubjectFor"/> of the account's uid, never the uid itself.</summary>
    [JsonPropertyName("sub")] public string Sub { get; set; } = "";
    [JsonPropertyName("issuedAt")] public DateTimeOffset IssuedAt { get; set; }
    [JsonPropertyName("offlineUntil")] public DateTimeOffset OfflineUntil { get; set; }
    [JsonPropertyName("products")] public Dictionary<string, LicencedProduct> Products { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("test")] public bool Test { get; set; }

    public const string Issuer = "kovati";

    /// <summary>
    /// When the licence stops counting: offlineUntil, but never more than 30 days after issuedAt. A server mistake that
    /// signed a far date cannot make a licence valid for longer than the rule. Plugins apply the same cap.
    /// </summary>
    [JsonIgnore] public DateTimeOffset ValidUntil => OfflineUntil < IssuedAt + LicenceRules.OfflineWindow ? OfflineUntil : IssuedAt + LicenceRules.OfflineWindow;

    public LicencedProduct? Product(string? id) => id is not null && Products.TryGetValue(id, out var p) ? p : null;

    /// <summary>
    /// The licence's subject for an account: the first 32 hex digits of SHA-256("kovati-licence:" + uid), as the
    /// account API computes it (functions/src/licence.ts subjectFor). The uid itself is never in the file.
    /// </summary>
    public static string SubjectFor(string uid) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("kovati-licence:" + uid)))[..32];

    /// <summary>Whether this licence was issued to the account with this uid.</summary>
    public bool IsFor(string? uid) => uid is { Length: > 0 } && string.Equals(Sub, SubjectFor(uid), StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false };

    /// <summary>The payload from its decoded bytes, or null with the reason.</summary>
    public static LicencePayload? Parse(byte[] utf8, out string? error)
    {
        error = null;
        try
        {
            var p = JsonSerializer.Deserialize<LicencePayload>(utf8, Json);
            if (p is null) { error = "payload is empty"; return null; }
            if (p.V != 1) { error = $"payload version {p.V} is not 1"; return null; }
            if (p.Iss != Issuer) { error = $"issuer is not {Issuer}"; return null; }
            if (string.IsNullOrWhiteSpace(p.Sub)) { error = "sub missing"; return null; }
            if (p.IssuedAt == default || p.OfflineUntil == default) { error = "issuedAt or offlineUntil missing"; return null; }
            p.Products = new Dictionary<string, LicencedProduct>(p.Products ?? [], StringComparer.OrdinalIgnoreCase);
            return p;
        }
        catch (JsonException ex) { error = $"payload is not a licence ({ex.Message})"; return null; }
    }
}

public sealed class LicencedProduct
{
    /// <summary>bought, granted, fab, pro or test.</summary>
    [JsonPropertyName("how")] public string How { get; set; } = "";
    /// <summary>A date ("2026-11-20") or an instant; kept as text and read leniently.</summary>
    [JsonPropertyName("since")] public string? Since { get; set; }
    [JsonPropertyName("maxMajor")] public int? MaxMajor { get; set; }

    public DateOnly? SinceDate =>
        Since is null ? null
        : DateOnly.TryParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
        : DateTimeOffset.TryParse(Since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? DateOnly.FromDateTime(t.UtcDateTime)
        : null;
}

/// <summary>RFC 4648 §5, without padding on the way out; padding is accepted on the way in.</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? TryDecode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var s = text.Trim().TrimEnd('=');
        if (s.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        s = s.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => "!" };
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    public static string EncodeUtf8(string text) => Encode(Encoding.UTF8.GetBytes(text));
}
