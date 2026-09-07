namespace Forge.Core.Cloud;

/// <summary>
/// The Firebase project behind accounts. The API key is the project's public
/// web key — it identifies, it does not authorise; Firestore rules and Auth do
/// the guarding. Overridable by environment for a test project or the emulators.
/// </summary>
public static class CloudConfig
{
    public const string ProjectId = "automation-forge-hq";

    public static string ApiKey =>
        Environment.GetEnvironmentVariable("FORGE_FIREBASE_API_KEY") ?? DefaultApiKey;

    /// <summary>The account app — where the hub sends a person to sign in, and where plugins are added to an account.</summary>
    public static string AppUrl =>
        (Environment.GetEnvironmentVariable("FORGE_ACCOUNT_URL") ?? "https://app.kovati.dev").TrimEnd('/');

    /// <summary>
    /// The account API: claims, downloads, checkout. Served on the app's own origin
    /// under /api. FORGE_API_URL points it at the emulator
    /// (http://127.0.0.1:5001/automation-forge-hq/europe-west1/api).
    /// </summary>
    public static string ApiUrl =>
        (Environment.GetEnvironmentVariable("FORGE_API_URL") ?? $"{AppUrl}/api").TrimEnd('/');

    /// <summary>Where a person goes to add or buy plugins for the account.</summary>
    public static string PluginsUrl => $"{AppUrl}/plugins/";

    /// <summary>
    /// Firebase Auth and Firestore endpoints. Against the emulators
    /// (FORGE_FIREBASE_EMULATOR=host:port for Auth, FORGE_FIRESTORE_EMULATOR for
    /// Firestore) they are plain http and the project's rules are still enforced.
    /// </summary>
    public static string IdentityToolkitUrl =>
        Environment.GetEnvironmentVariable("FORGE_FIREBASE_EMULATOR") is { Length: > 0 } e
            ? $"http://{e}/identitytoolkit.googleapis.com/v1"
            : "https://identitytoolkit.googleapis.com/v1";

    public static string SecureTokenUrl =>
        Environment.GetEnvironmentVariable("FORGE_FIREBASE_EMULATOR") is { Length: > 0 } e
            ? $"http://{e}/securetoken.googleapis.com/v1"
            : "https://securetoken.googleapis.com/v1";

    public static string FirestoreUrl =>
        Environment.GetEnvironmentVariable("FORGE_FIRESTORE_EMULATOR") is { Length: > 0 } e
            ? $"http://{e}/v1"
            : "https://firestore.googleapis.com/v1";

    public static bool Configured => ApiKey.Length > 0;

    // From `firebase apps:sdkconfig WEB` for automation-forge-hq (web app 1:642032747874:web:d3615136271bfda1bb983b).
    private const string DefaultApiKey = "AIzaSyBjrX8iTq1sTx4NFbnj4eu06oPYu_2olLc";
}
