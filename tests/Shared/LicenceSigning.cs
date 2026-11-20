using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Core.Licence;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Forge.Licence.Testing;

/// <summary>
/// Signs licences the way the account API does, with a THROWAWAY key made at run time: for the unit tests, the
/// headless screens and tools/licence-fake-server. No private key is ever written into the repository, and no key
/// made here is trusted by a release build (a release trusts only <see cref="LicenceKeys.BuiltIn"/>; dev keys are
/// read only in Debug builds or under FORGE_HUB_INSTANCE, and never under the kid "k1").
/// </summary>
public sealed class TestLicenceKey
{
    private readonly Ed25519PrivateKeyParameters _private;

    private TestLicenceKey(string kid, Ed25519PrivateKeyParameters key)
    {
        Kid = kid;
        _private = key;
        PublicKey = key.GeneratePublicKey().GetEncoded();
    }

    public string Kid { get; }
    public byte[] PublicKey { get; }
    public string PublicKeyBase64Url => Base64Url.Encode(PublicKey);

    /// <summary>The value for FORGE_LICENCE_DEV_KEYS.</summary>
    public string DevKeysValue => $"{Kid}={PublicKeyBase64Url}";

    public static TestLicenceKey Generate(string kid = "dev1") => new(kid, new Ed25519PrivateKeyParameters(new SecureRandom()));

    public static TestLicenceKey FromSeed(string kid, byte[] seed32) => new(kid, new Ed25519PrivateKeyParameters(seed32, 0));

    public LicenceKeyRing Ring() => new(new Dictionary<string, string>(), new Dictionary<string, string> { [Kid] = PublicKeyBase64Url });

    public byte[] SignBytes(byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _private);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    /// <summary>The envelope for these exact payload bytes, as the API would send it.</summary>
    public string Envelope(string payloadJson, string? kid = null)
    {
        var bytes = Encoding.UTF8.GetBytes(payloadJson);
        return JsonSerializer.Serialize(new
        {
            format = LicenceEnvelope.FormatName,
            v = 1,
            kid = kid ?? Kid,
            payload = Base64Url.Encode(bytes),
            sig = Base64Url.Encode(SignBytes(bytes)),
        });
    }

    public string Envelope(string sub, DateTimeOffset issuedAt, IDictionary<string, (string how, string since, int maxMajor)>? products = null, bool test = false) =>
        Envelope(Payload(sub, issuedAt, products, test));

    /// <summary>A payload in the contract's shape: offlineUntil = issuedAt + 30 days.</summary>
    public static string Payload(string sub, DateTimeOffset issuedAt, IDictionary<string, (string how, string since, int maxMajor)>? products = null,
        bool test = false, DateTimeOffset? offlineUntil = null)
    {
        static string T(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var map = (products ?? new Dictionary<string, (string, string, int)>())
            .ToDictionary(kv => kv.Key, kv => new { how = kv.Value.how, since = kv.Value.since, maxMajor = kv.Value.maxMajor });
        return JsonSerializer.Serialize(new
        {
            v = 1,
            iss = "kovati",
            sub,
            issuedAt = T(issuedAt),
            offlineUntil = T(offlineUntil ?? issuedAt.AddDays(30)),
            products = map,
            test,
        });
    }
}
