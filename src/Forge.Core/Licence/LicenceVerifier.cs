using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Forge.Core.Licence;

/// <summary>A licence that verified: its envelope as stored, its payload, and whether a dev key signed it.</summary>
public sealed record VerifiedLicence(LicenceEnvelope Envelope, LicencePayload Payload, bool SignedWithDevKey);

/// <summary>
/// Ed25519 over the decoded payload bytes, with the key named by kid. Unknown kid, placeholder key, a signature that is
/// not 64 bytes, a payload that is not base64url: all fail. Nothing here reads the clock; the rules do that.
/// </summary>
public static class LicenceVerifier
{
    public static VerifiedLicence? Verify(string json, LicenceKeyRing keys, out string? error)
    {
        var env = LicenceEnvelope.Parse(json, out error);
        return env is null ? null : Verify(env, keys, out error);
    }

    public static VerifiedLicence? Verify(LicenceEnvelope env, LicenceKeyRing keys, out string? error)
    {
        error = null;
        if (keys.Find(env.Kid) is not { } key) { error = $"no usable key for kid {env.Kid}"; return null; }
        var payload = Base64Url.TryDecode(env.Payload);
        if (payload is null) { error = "payload is not base64url"; return null; }
        var sig = Base64Url.TryDecode(env.Sig);
        if (sig is not { Length: 64 }) { error = "signature is not 64 bytes of base64url"; return null; }
        if (!Ed25519Verify(key.Key, payload, sig)) { error = "signature does not verify"; return null; }
        var parsed = LicencePayload.Parse(payload, out error);
        return parsed is null ? null : new VerifiedLicence(env, parsed, key.IsDev);
    }

    public static bool Ed25519Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (ArgumentException) { return false; }
    }
}
