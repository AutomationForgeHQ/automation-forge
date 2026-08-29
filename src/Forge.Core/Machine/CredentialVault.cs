using System.Runtime.InteropServices;
using System.Text;

namespace Forge.Core.Machine;

/// <summary>Where a key was found, or why it was not.</summary>
public enum KeySource
{
    /// <summary>Nowhere. The plugin will behave as if it had never been given one.</summary>
    None,

    /// <summary>The Windows Credential Manager — the same entry the editor writes.</summary>
    Vault,

    /// <summary>An environment variable, which the plugins consult when the vault has nothing.</summary>
    Environment,
}

/// <summary>
/// The Windows Credential Manager, addressed exactly as the plugins address it.
///
/// **This is what makes "set it in either place" true rather than aspirational.** The editor stores
/// a key with CredWriteW against a generic target such as `MotionForge/Uthana`; this reads and
/// writes the identical entry through the identical calls. There is no second store, no sync, and
/// nothing to go out of step — the vault is machine-wide and both surfaces are looking at one row.
///
/// Windows only. Everything here reports "no vault backend" elsewhere rather than pretending, and
/// the environment variable remains the documented way in on those platforms.
/// </summary>
public static class CredentialVault
{
    private const int GenericCredential = 1;
    private const int PersistLocalMachine = 2;

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Whether this key is available to the plugin right now, and from where.</summary>
    public static KeySource Find(DeclaredKey key)
    {
        if (IsSupported && Exists(key.VaultEntry)) return KeySource.Vault;

        if (!string.IsNullOrWhiteSpace(key.EnvironmentVariable)
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key.EnvironmentVariable)))
        {
            return KeySource.Environment;
        }

        return KeySource.None;
    }

    /// <summary>One line for the UI, in the same words the editor's Keys page uses.</summary>
    public static string Describe(DeclaredKey key) => Find(key) switch
    {
        KeySource.Vault => "Stored in Windows Credential Manager",
        KeySource.Environment => $"Set by the environment variable {key.EnvironmentVariable}",
        _ when !IsSupported => "No credential vault on this platform",
        _ => "Not set",
    };

    /// <summary>
    /// Store or replace the secret. Never read back into the interface.
    ///
    /// Persisted to the local machine rather than the session, which is what the editor does — a key
    /// that vanished at logout would be indistinguishable from one that was never set.
    /// </summary>
    public static bool Store(DeclaredKey key, string secret, out string error)
    {
        error = "";

        if (!IsSupported)
        {
            error = "This platform has no credential vault. Set the environment variable instead.";
            return false;
        }

        if (string.IsNullOrEmpty(secret))
        {
            error = "Nothing to store.";
            return false;
        }

        var blob = Encoding.Unicode.GetBytes(secret);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);

        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            var credential = new CREDENTIAL
            {
                Type = GenericCredential,
                TargetName = key.VaultEntry,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPtr,
                Persist = PersistLocalMachine,
                UserName = "AutomationForge",
            };

            if (!CredWriteW(ref credential, 0))
            {
                error = $"Windows refused to store it (error {Marshal.GetLastWin32Error()}).";
                return false;
            }

            return true;
        }
        finally
        {
            // Zero it before releasing: a secret left in freed memory is a secret still in memory.
            for (var i = 0; i < blob.Length; i++) blob[i] = 0;
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    /// <summary>
    /// Forget the stored key.
    ///
    /// An environment variable, if one is set, still applies afterwards — which is why the UI has to
    /// report where a key came from rather than only whether it is there.
    /// </summary>
    public static bool Clear(DeclaredKey key, out string error)
    {
        error = "";

        if (!IsSupported)
        {
            error = "This platform has no credential vault.";
            return false;
        }

        if (!Exists(key.VaultEntry)) return true;

        if (!CredDeleteW(key.VaultEntry, GenericCredential, 0))
        {
            error = $"Windows refused to remove it (error {Marshal.GetLastWin32Error()}).";
            return false;
        }

        return true;
    }

    private static bool Exists(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;

        if (!CredReadW(target, GenericCredential, 0, out var handle)) return false;

        CredFree(handle);
        return true;
    }

    // ---------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int reserved, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
