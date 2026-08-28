using Forge.Core.Manifest;

namespace Forge.Core.Entitlements;

public sealed class EntitlementException(string message) : Exception(message);

/// <summary>
/// Decides whether a paid download can be issued. Free plugins never come here —
/// their URL is public in the manifest. The anonymous provider is what every
/// surface starts with; the account-backed one arrives with the Pro backend
/// (HUB_PLAN.md §2–§3) and returns a short-lived signed URL for an owned plugin.
/// </summary>
public interface IEntitlementProvider
{
    bool IsSignedIn { get; }
    string? AccountLabel { get; }

    /// <summary>Whether the current identity owns the plugin. Free plugins are always owned.</summary>
    Task<bool> OwnsAsync(PluginInfo plugin, CancellationToken ct = default);

    /// <summary>A download URL for a paid version, or null when not entitled.</summary>
    Task<string?> ResolveDownloadUrlAsync(PluginInfo plugin, VersionInfo version, CancellationToken ct = default);
}

/// <summary>No account. Everything free, nothing paid.</summary>
public sealed class AnonymousEntitlements : IEntitlementProvider
{
    public bool IsSignedIn => false;
    public string? AccountLabel => null;
    public Task<bool> OwnsAsync(PluginInfo plugin, CancellationToken ct = default) => Task.FromResult(!plugin.IsPaid);
    public Task<string?> ResolveDownloadUrlAsync(PluginInfo plugin, VersionInfo version, CancellationToken ct = default) => Task.FromResult<string?>(null);
}
