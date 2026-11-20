namespace Forge.Core.Licence;

/// <summary>
/// The public keys a licence may be signed with, by kid. Built into the Hub and the CLI; the private halves live only
/// in the account API's secret store.
///
/// **Where the key goes.** <see cref="K1"/> holds the base64url of the raw 32-byte Ed25519 public key that
/// `GET {api}/licence/keys` lists for kid "k1" (pasted 20 November 2026). A new key later is one more line in
/// <see cref="BuiltIn"/> (keep the old one until no licence signed with it can still be valid: 30 days); a kid whose key
/// is not known yet can stand in the table as <see cref="Placeholder"/>, which verifies nothing.
///
/// A key that is the placeholder, or that does not decode to exactly 32 bytes, verifies nothing.
/// </summary>
public static class LicenceKeys
{
    /// <summary>Marks a kid whose real key has not been pasted yet. Verifies nothing.</summary>
    public const string Placeholder = "PASTE-K1-PUBLIC-KEY-BASE64URL-HERE";

    /// <summary>
    /// kid "k1": the account API's first licence key (base64url, raw 32 bytes), as served at
    /// https://app.kovati.dev/api/licence/keys (pasted 20 November 2026).
    /// </summary>
    public const string K1 = "NfOKjVJCzXOHKoc6EW24UqZzIYQ6HRJ-eoMysFYm76g";

    /// <summary>Every key a release build trusts.</summary>
    public static IReadOnlyDictionary<string, string> BuiltIn { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["k1"] = K1,
    };

    /// <summary>
    /// Extra keys for development and tests: <c>FORGE_LICENCE_DEV_KEYS=kid=base64url;kid2=base64url</c>.
    /// </summary>
    public const string DevKeysVariable = "FORGE_LICENCE_DEV_KEYS";

    /// <summary>
    /// Whether <see cref="DevKeysVariable"/> is read at all: in a Debug build of Forge.Core, or when the Hub runs as a
    /// named development instance (FORGE_HUB_INSTANCE set, as every dev instance is). A release build of an installed
    /// Hub has neither and ignores the variable completely. A dev key never replaces a built-in kid.
    /// </summary>
    public static bool DevKeysAllowed =>
#if DEBUG
        true;
#else
        Environment.GetEnvironmentVariable("FORGE_HUB_INSTANCE") is { Length: > 0 };
#endif

    /// <summary>The dev keys from the environment, when allowed; empty otherwise.</summary>
    public static IReadOnlyDictionary<string, string> DevKeys()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!DevKeysAllowed || Environment.GetEnvironmentVariable(DevKeysVariable) is not { Length: > 0 } text) return keys;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1) continue;
            var kid = part[..eq].Trim();
            if (BuiltIn.ContainsKey(kid)) continue;
            keys[kid] = part[(eq + 1)..].Trim();
        }
        return keys;
    }

    /// <summary>The raw 32 bytes of a usable key, or null for the placeholder or anything that is not one.</summary>
    public static byte[]? Usable(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key == Placeholder) return null;
        var bytes = Base64Url.TryDecode(key);
        if (bytes is not { Length: 32 }) return null;
        if (bytes.All(b => b == 0)) return null;
        return bytes;
    }
}

/// <summary>The keys one verification may use: the built-in ones, and (when allowed) the dev ones.</summary>
public sealed class LicenceKeyRing
{
    private readonly IReadOnlyDictionary<string, string> _builtIn;
    private readonly IReadOnlyDictionary<string, string> _dev;

    public LicenceKeyRing(IReadOnlyDictionary<string, string> builtIn, IReadOnlyDictionary<string, string>? dev = null)
    {
        _builtIn = builtIn;
        _dev = dev ?? new Dictionary<string, string>();
    }

    /// <summary>The keys of this build: <see cref="LicenceKeys.BuiltIn"/> and the dev keys this run allows.</summary>
    public static LicenceKeyRing Default() => new(LicenceKeys.BuiltIn, LicenceKeys.DevKeys());

    /// <summary>The key for a kid and whether it came from the dev list. Null when unknown or unusable.</summary>
    public (byte[] Key, bool IsDev)? Find(string kid)
    {
        if (_builtIn.TryGetValue(kid, out var b)) return LicenceKeys.Usable(b) is { } k ? (k, false) : null;
        if (_dev.TryGetValue(kid, out var d)) return LicenceKeys.Usable(d) is { } k ? (k, true) : null;
        return null;
    }
}
