using Forge.Core.Cloud;
using Forge.Core.Licence;
using Forge.Core.Manifest;

namespace Forge.Core.Catalogue;

/// <summary>How this account may have one plugin.</summary>
public enum PaidAccess
{
    /// <summary>Not a paid plugin: free, added to the account on the first install.</summary>
    Free,
    /// <summary>Paid, and the account gets what it got before free tiers: it holds it licensed, or (no free tier) it holds
    /// it at all. Any build, the newest.</summary>
    Full,
    /// <summary>Paid, not held licensed, and it has a free tier with a build for this engine: that build, which runs as Lite (or a trial).</summary>
    FreeTier,
    /// <summary>Paid with a free tier, but every build for this engine is below its floor: only a licence downloads them.</summary>
    NeedsLicence,
    /// <summary>Paid, no free tier, not on the account.</summary>
    NotOwned,
}

/// <summary>
/// What the free-tier rules read: the slice of Kovati Hub's planner inputs (PlanInputs) that the 0.4.x Hub has. There
/// are no tracks or catalogue here; the rules are the same.
/// </summary>
public sealed record AccessInputs
{
    /// <summary>The target's engine version, "5.8".</summary>
    public required string Engine { get; init; }
    public string Channel { get; init; } = Settings.Stable;
    public IReadOnlySet<string> Owned { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// The paid plugins the account holds by a licensed route (GET me `licensed`, and what the licence on this computer
    /// lists as current: <see cref="FreeTiers.LicensedNow"/>). Null when the account service did not say: then
    /// <see cref="Owned"/> counts as licensed, the rule before free tiers.
    /// </summary>
    public IReadOnlySet<string>? Licensed { get; init; }
    /// <summary>The account service's offers (GET catalogue): each paid plugin's free tier and its builds. Null until read.</summary>
    public AccountCatalogue? Offers { get; init; }
}

/// <summary>
/// A paid plugin's free tier (the account API's `tier`, `tierSince` and per-build `freeTier`; GET me `licensed`), ported
/// from Kovati Hub 0.5.0-beta.7 (src/Forge.Core/Catalogue/FreeTiers.cs). A paid plugin is installable when the account
/// holds it licensed, or when its offer has a tier and the build to install has `freeTier: true`. Without those fields
/// (an older API, the account catalogue not read yet) everything is as it was in 0.4.4: a paid plugin installs only when
/// the account holds it.
///
/// One difference from the beta: a paid plugin without a free tier is Full only when the account holds it. The beta also
/// counts a copy installed on the target; 0.4.4 never did, and a product without a free tier behaves exactly as in 0.4.4.
/// </summary>
public static class FreeTiers
{
    /// <summary>Held by a licensed route. When the account service did not say (<see cref="AccessInputs.Licensed"/> null), holding it is enough.</summary>
    public static bool IsLicensed(string id, AccessInputs inputs) =>
        inputs.Licensed is { } licensed ? licensed.Contains(id) : inputs.Owned.Contains(id);

    /// <summary>"lite", "trial", or null: the offer's free tier.</summary>
    public static string? TierOf(string id, AccessInputs inputs) => inputs.Offers?.Plugin(id)?.FreeTier;

    /// <summary>The first version a free-tier download may have, when the offer says.</summary>
    public static string? FloorOf(string id, AccessInputs inputs) => inputs.Offers?.Plugin(id)?.TierSince;

    public static PaidAccess AccessOf(PluginInfo plugin, AccessInputs inputs)
    {
        if (!plugin.IsPaid) return PaidAccess.Free;
        if (IsLicensed(plugin.Id, inputs)) return PaidAccess.Full;
        if (TierOf(plugin.Id, inputs) is null)
            return inputs.Owned.Contains(plugin.Id) ? PaidAccess.Full : PaidAccess.NotOwned;
        return FreeTierBuild(plugin, inputs) is not null ? PaidAccess.FreeTier : PaidAccess.NeedsLicence;
    }

    /// <summary>The build this account may install on this engine and channel, or null when it may install none.</summary>
    public static VersionInfo? BuildFor(PluginInfo plugin, AccessInputs inputs) => AccessOf(plugin, inputs) switch
    {
        PaidAccess.Free or PaidAccess.Full => plugin.Latest(inputs.Engine, inputs.Channel),
        PaidAccess.FreeTier => FreeTierBuild(plugin, inputs),
        _ => null,
    };

    /// <summary>The newest build for this engine and channel that the free tier serves; never one below the floor.</summary>
    public static VersionInfo? FreeTierBuild(PluginInfo plugin, AccessInputs inputs)
    {
        if (inputs.Offers?.Plugin(plugin.Id) is not { FreeTier: not null } offer) return null;
        return plugin.Versions
            .Where(v => v.Engine == inputs.Engine && (inputs.Channel == Settings.Nightly || v.Channel == Settings.Stable))
            .Where(offer.ServesFreeTier)
            .OrderByDescending(v => v.ReleasedAt, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// The paid plugins this account holds licensed: what the account service said (GET me `licensed`), and what the
    /// licence on this computer lists as current, so a purchase counts as soon as the licence refresh brings it, before
    /// what the account holds is read again. Null when the account service did not say (Kovati Hub: LicensedNow) and the
    /// licence lists nothing current: then holding counts, the rule before free tiers.
    ///
    /// When the account service did not say (offline: the window could not read what the account holds either) but the
    /// licence on this computer lists a product as current (or in its offline grace), that product counts as licensed,
    /// beside whatever the account is known to hold (<paramref name="owned"/>). Otherwise a bought copy read as Lite with
    /// "Buy Full" next to "Offline, 5 days left".
    /// </summary>
    public static IReadOnlySet<string>? LicensedNow(IReadOnlySet<string>? fromAccount, LicenceSnapshot? licence, IReadOnlySet<string>? owned = null)
    {
        var fromLicence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (licence is not null)
            foreach (var id in licence.Payload?.Products.Keys ?? Enumerable.Empty<string>())
                if (licence.For(id).CountsAsLicensed) fromLicence.Add(id);
        if (fromAccount is null)
        {
            if (fromLicence.Count == 0) return null;
            fromLicence.UnionWith(owned ?? Enumerable.Empty<string>());
            return fromLicence;
        }
        var set = new HashSet<string>(fromAccount, StringComparer.OrdinalIgnoreCase);
        set.UnionWith(fromLicence);
        return set;
    }

    /// <summary>"Lite" or "trial", for a sentence ("runs as Lite", "installs as a trial").</summary>
    public static string Word(string? tier) => tier == "trial" ? "trial" : "Lite";

    /// <summary>The sentence for a free tier whose builds for this engine are all below its floor (the account service's words).</summary>
    public static string NeedsLicenceLine(string name, string? version, string? tier, string? floor) =>
        version is not null && floor is not null
            ? $"Version {version} of {name} needs a licence. The free {(tier == "trial" ? "trial" : "Lite tier")} starts with {floor}."
            : $"{name} needs a licence.";
}
