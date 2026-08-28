namespace Forge.Core.Cloud;

/// <summary>
/// The Firebase project behind accounts. The API key is the project's public
/// web key — it identifies, it does not authorise; Firestore rules and Auth do
/// the guarding. Overridable by environment for a test project.
/// </summary>
public static class CloudConfig
{
    public const string ProjectId = "automation-forge-hq";

    public static string ApiKey =>
        Environment.GetEnvironmentVariable("FORGE_FIREBASE_API_KEY") ?? DefaultApiKey;

    /// <summary>The account app — where the hub sends a person to sign in.</summary>
    public static string AppUrl =>
        Environment.GetEnvironmentVariable("FORGE_ACCOUNT_URL") ?? "https://automation-forge-app.web.app";

    public static bool Configured => ApiKey.Length > 0;

    // From `firebase apps:sdkconfig WEB` for automation-forge-hq (web app 1:642032747874:web:d3615136271bfda1bb983b).
    private const string DefaultApiKey = "AIzaSyBjrX8iTq1sTx4NFbnj4eu06oPYu_2olLc";
}
