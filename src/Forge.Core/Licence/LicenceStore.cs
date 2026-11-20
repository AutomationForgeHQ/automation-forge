using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core.Licence;

/// <summary>What the last licence calls did, beside the licence: licence-check.json. Never read by plugins.</summary>
public sealed class LicenceCheck
{
    /// <summary>The last time a licence for this account verified (stored or not newer).</summary>
    [JsonPropertyName("lastSuccessAt")] public DateTimeOffset? LastSuccessAt { get; set; }
    /// <summary>The last time the server answered at all (any status).</summary>
    [JsonPropertyName("lastServerAnswerAt")] public DateTimeOffset? LastServerAnswerAt { get; set; }
    [JsonPropertyName("lastAttemptAt")] public DateTimeOffset? LastAttemptAt { get; set; }
    [JsonPropertyName("lastOutcome")] public LicenceFetchOutcome? LastOutcome { get; set; }
    /// <summary>A 429's Retry-After: no call, automatic or asked for, is made before this time.</summary>
    [JsonPropertyName("retryNotBefore")] public DateTimeOffset? RetryNotBefore { get; set; }
    /// <summary>The account these times are for: a different account starts from nothing.</summary>
    [JsonPropertyName("sub")] public string? Sub { get; set; }
}

/// <summary>What happened to a licence offered to the store.</summary>
public enum LicenceSave
{
    Stored,
    NotNewer,
    Invalid,
    WrongAccount,
    DevKeyRefused,
    /// <summary>It verified, but the file could not be replaced (a reader held it open for over half a second). The old file stays.</summary>
    WriteFailed,
}

/// <summary>
/// licence.json in the data folder (%LOCALAPPDATA%\AutomationForge, or FORGE_DATA_DIR): the envelope exactly as the
/// account API sent it, written through a temporary file and a move, replaced only by a licence that verifies, is for
/// the signed-in account and was issued later. This path and format are a contract with the paid plugins
/// (HUB_V2_NOTES.md, "The licence file").
/// </summary>
public sealed class LicenceStore
{
    public const string FileName = "licence.json";
    public const string CheckFileName = "licence-check.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _dir;
    private readonly LicenceKeyRing _keys;
    private readonly bool _isDefaultDataDir;

    /// <param name="dir">The data folder; <see cref="Paths.DataDir"/> when null.</param>
    /// <param name="keys">The trusted keys; this build's when null.</param>
    /// <param name="isDefaultDataDir">Whether <paramref name="dir"/> is the real %LOCALAPPDATA%\AutomationForge. Null: worked out from FORGE_DATA_DIR.</param>
    public LicenceStore(string? dir = null, LicenceKeyRing? keys = null, bool? isDefaultDataDir = null)
    {
        _dir = dir ?? Paths.DataDir;
        _keys = keys ?? LicenceKeyRing.Default();
        _isDefaultDataDir = isDefaultDataDir ?? (dir is null && Environment.GetEnvironmentVariable("FORGE_DATA_DIR") is not { Length: > 0 });
    }

    public string FilePath => Path.Combine(_dir, FileName);
    public string CheckPath => Path.Combine(_dir, CheckFileName);
    public LicenceKeyRing Keys => _keys;

    /// <summary>The stored licence when it verifies; null when there is none or it does not (the reason in <paramref name="error"/>).</summary>
    public VerifiedLicence? Load(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(FilePath)) { error = "no licence file"; return null; }
            return LicenceVerifier.Verify(File.ReadAllText(FilePath, Encoding.UTF8), _keys, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; return null; }
    }

    public VerifiedLicence? Load() => Load(out _);

    /// <summary>
    /// Offer a licence as received. Stored when it verifies, its sub is <paramref name="accountUid"/>, and the stored one
    /// is missing, unreadable, for another account, or issued earlier. A licence verified only by a dev key is never
    /// written into the real data folder (only into a FORGE_DATA_DIR one).
    /// </summary>
    public LicenceSave Offer(string body, string accountUid, out VerifiedLicence? verified) =>
        Offer(Encoding.UTF8.GetBytes(body), accountUid, out verified);

    /// <inheritdoc cref="Offer(string, string, out VerifiedLicence?)"/>
    public LicenceSave Offer(byte[] body, string accountUid, out VerifiedLicence? verified)
    {
        verified = LicenceVerifier.Verify(Encoding.UTF8.GetString(body), _keys, out _);
        if (verified is null) return LicenceSave.Invalid;
        if (!string.Equals(verified.Payload.Sub, accountUid, StringComparison.Ordinal)) return LicenceSave.WrongAccount;
        if (verified.SignedWithDevKey && _isDefaultDataDir) return LicenceSave.DevKeyRefused;
        var current = Load();
        if (current is not null && string.Equals(current.Payload.Sub, accountUid, StringComparison.Ordinal)
            && verified.Payload.IssuedAt <= current.Payload.IssuedAt)
            return LicenceSave.NotNewer;
        try { WriteAtomically(FilePath, body); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return LicenceSave.WriteFailed; }
        return LicenceSave.Stored;
    }

    /// <summary>Remove the licence (sign-out, a refused account, a licence for someone else). The check record goes with it.</summary>
    public void Delete()
    {
        foreach (var f in new[] { FilePath, CheckPath })
            try { if (File.Exists(f)) File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A stored licence that does not belong to this account is removed; returns true when it removed one.</summary>
    public bool DiscardIfNotFor(string? accountUid)
    {
        var current = Load();
        if (current is null) return false;
        if (accountUid is not null && string.Equals(current.Payload.Sub, accountUid, StringComparison.Ordinal)) return false;
        Delete();
        return true;
    }

    public LicenceCheck LoadCheck()
    {
        try
        {
            if (File.Exists(CheckPath)) return JsonSerializer.Deserialize<LicenceCheck>(File.ReadAllText(CheckPath), Json) ?? new LicenceCheck();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new LicenceCheck();
    }

    public void SaveCheck(LicenceCheck check)
    {
        try { WriteAtomically(CheckPath, JsonSerializer.SerializeToUtf8Bytes(check, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A temporary file beside the target, flushed, then moved over it: a reader sees the old file or the new one, never
    /// half of one. Windows refuses to replace a file another program has open, so the move is tried for half a second
    /// (a plugin reads the file whole and closes it at once); after that the old file stays and this throws.
    /// </summary>
    internal static void WriteAtomically(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            for (var attempt = 1; ; attempt++)
            {
                try { File.Move(temp, path, overwrite: true); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 6) { Thread.Sleep(100); }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }
}
