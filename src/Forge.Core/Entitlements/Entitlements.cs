using Forge.Core.Manifest;

namespace Forge.Core.Entitlements;

public sealed class EntitlementException(string message, string? code = null) : Exception(message)
{
    /// <summary>The API's short reason — not-claimed, not-owned, unauthenticated — when there is one.</summary>
    public string? Code { get; } = code;
}

/// <summary>
/// Decides whether a download can be issued, for every plugin, free or paid.
///
/// Since 2026-09-06 nothing installs without an account: a plugin is on an
/// account or it is not, and the manifest's public URL is a locator for the
/// backend, never a download for a client. Free plugins are *claimed* — added
/// to the account with one request, which the install path does by itself —
/// and paid ones are bought on the account site or linked from Fab. The
/// anonymous provider owns nothing and can fetch nothing; it exists so every
/// surface has something to hold before a person signs in.
/// </summary>
public interface IEntitlementProvider
{
    bool IsSignedIn { get; }
    string? AccountLabel { get; }

    /// <summary>Whether the current account holds the plugin.</summary>
    Task<bool> OwnsAsync(PluginInfo plugin, CancellationToken ct = default);

    /// <summary>
    /// Add free plugins to the account — the plugin and the free members of its
    /// dependency closure. Returns what was newly added. Paid members are left
    /// alone and named in <see cref="ClaimResult.Paid"/>.
    /// </summary>
    Task<ClaimResult> ClaimAsync(IEnumerable<string> pluginIds, CancellationToken ct = default);

    /// <summary>
    /// A short-lived download URL for a version the account holds. A free plugin
    /// not yet on the account is claimed first. Throws <see cref="EntitlementException"/>
    /// when signed out, when a paid plugin is not owned, or when the account
    /// service cannot be reached.
    /// </summary>
    Task<ResolvedDownload> ResolveDownloadAsync(PluginInfo plugin, VersionInfo version, CancellationToken ct = default);
}

public sealed record ClaimResult(IReadOnlyList<string> Added, IReadOnlyList<string> AlreadyOwned, IReadOnlyList<string> Paid);

public sealed record ResolvedDownload(string Url, string? Sha256, long? Size, DateTimeOffset ExpiresAt);

/// <summary>No account. Owns nothing, fetches nothing.</summary>
public sealed class AnonymousEntitlements : IEntitlementProvider
{
    public static string SignInMessage =>
        $"Sign in to install. Plugins are added to your account at {Cloud.CloudConfig.AppUrl} — free ones with one click.";

    public bool IsSignedIn => false;
    public string? AccountLabel => null;
    public Task<bool> OwnsAsync(PluginInfo plugin, CancellationToken ct = default) => Task.FromResult(false);
    public Task<ClaimResult> ClaimAsync(IEnumerable<string> pluginIds, CancellationToken ct = default) =>
        throw new EntitlementException(SignInMessage, "unauthenticated");
    public Task<ResolvedDownload> ResolveDownloadAsync(PluginInfo plugin, VersionInfo version, CancellationToken ct = default) =>
        throw new EntitlementException(SignInMessage, "unauthenticated");
}
