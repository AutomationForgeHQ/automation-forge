using System.Security.Cryptography;

namespace Forge.Core;

public static class Checksums
{
    public static string Sha256Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool Matches(string file, string? expected) =>
        string.IsNullOrEmpty(expected) || string.Equals(Sha256Of(file), expected, StringComparison.OrdinalIgnoreCase);
}
