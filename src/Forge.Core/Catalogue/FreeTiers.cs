using Forge.Core.Manifest;

namespace Forge.Core.Catalogue;

/// <summary>How this account may have one component.</summary>
public enum PaidAccess
{
    /// <summary>Not a paid plugin: free, added to the account on the first install.</summary>
    Free,
    /// <summary>Paid, and the account gets what it got before free tiers: it holds it licensed, or (no free tier) it holds
    /// it at all, or it is installed here already. Any build, the newest.</summary>
    Full,
    /// <summary>Paid, not held licensed, and it has a free tier with a build for this engine: that build, which runs as Lite (or a trial).</summary>
    FreeTier,
    /// <summary>Paid with a free tier, but every build for this engine is below its floor: only a licence downloads them.</summary>
    NeedsLicence,
    /// <summary>Paid, no free tier, not on the account.</summary>
    NotOwned,
}

/// <summary>
/// A paid plugin's free tier (the account API's `tier`, `tierSince` and per-build `freeTier`; GET me `licensed`), as pure
/// functions over <see cref="PlanInputs"/>. A paid plugin is installable when the account holds it licensed, or when its
/// offer has a tier and the build to install has `freeTier: true`. Without those fields (an older API, the account
/// catalogue not read yet) everything is as it was: a paid plugin installs only when the account holds it.
/// </summary>
public static class FreeTiers
{
    /// <summary>Held by a licensed route. When the account service did not say (<see cref="PlanInputs.Licensed"/> null), holding it is enough.</summary>
    public static bool IsLicensed(string id, PlanInputs inputs) =>
        inputs.Licensed is { } licensed ? licensed.Contains(id) : inputs.Owned.Contains(id);

    /// <summary>"lite", "trial", or null: the offer's free tier.</summary>
    public static string? TierOf(string id, PlanInputs inputs) => inputs.Offers?.Plugin(id)?.FreeTier;

    /// <summary>The first version a free-tier download may have, when the offer says.</summary>
    public static string? FloorOf(string id, PlanInputs inputs) => inputs.Offers?.Plugin(id)?.TierSince;

    public static PaidAccess AccessOf(PluginInfo plugin, PlanInputs inputs)
    {
        if (!plugin.IsPaid) return PaidAccess.Free;
        if (IsLicensed(plugin.Id, inputs)) return PaidAccess.Full;
        if (TierOf(plugin.Id, inputs) is null)
            return inputs.Owned.Contains(plugin.Id) || inputs.Has(plugin.Id) ? PaidAccess.Full : PaidAccess.NotOwned;
        return FreeTierBuild(plugin, inputs) is not null ? PaidAccess.FreeTier : PaidAccess.NeedsLicence;
    }

    /// <summary>The build this account may install on this engine and channel, or null when it may install none.</summary>
    public static VersionInfo? BuildFor(PluginInfo plugin, PlanInputs inputs) => AccessOf(plugin, inputs) switch
    {
        PaidAccess.Free or PaidAccess.Full => plugin.Latest(inputs.Engine, inputs.Channel),
        PaidAccess.FreeTier => FreeTierBuild(plugin, inputs),
        _ => null,
    };

    /// <summary>The newest build for this engine and channel that the free tier serves; never one below the floor.</summary>
    public static VersionInfo? FreeTierBuild(PluginInfo plugin, PlanInputs inputs)
    {
        if (inputs.Offers?.Plugin(plugin.Id) is not { FreeTier: not null } offer) return null;
        return plugin.Versions
            .Where(v => v.Engine == inputs.Engine && (inputs.Channel == Settings.Nightly || v.Channel == Settings.Stable))
            .Where(offer.ServesFreeTier)
            .OrderByDescending(v => v.ReleasedAt, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>"Lite" or "trial", for a sentence ("runs as Lite", "installs as a trial").</summary>
    public static string Word(string? tier) => tier == "trial" ? "trial" : "Lite";

    /// <summary>The planner's sentence for a free tier whose builds for this engine are all below its floor.</summary>
    public static string NeedsLicenceLine(string name, string? version, string? tier, string? floor) =>
        version is not null && floor is not null
            ? $"Version {version} of {name} needs a licence. The free {(tier == "trial" ? "trial" : "Lite tier")} starts with {floor}."
            : $"{name} needs a licence.";
}
