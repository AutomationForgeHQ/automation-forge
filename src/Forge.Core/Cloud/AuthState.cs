using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core.Cloud;

/// <summary>What the hub keeps of a sign-in: who, and the token that renews the session.</summary>
public sealed record StoredAccount(
    [property: JsonPropertyName("uid")] string Uid,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("refreshToken")] string RefreshToken);

/// <summary>
/// %LOCALAPPDATA%\AutomationForge\account.bin — the stored sign-in, protected
/// with the Windows user's DPAPI key so another account on the machine cannot
/// read it. Elsewhere it is plain, with the file's own permissions.
/// </summary>
public static class AuthState
{
    private static string FilePath => Path.Combine(Paths.DataDir, "account.bin");

    public static StoredAccount? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var bytes = File.ReadAllBytes(FilePath);
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredAccount>(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            return null; // an unreadable session is the same as none
        }
    }

    public static void Save(StoredAccount account)
    {
        Directory.CreateDirectory(Paths.DataDir);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(account);
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, bytes);
    }

    public static void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
