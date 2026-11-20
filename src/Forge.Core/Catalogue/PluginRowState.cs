using Forge.Core.Licence;
using Forge.Core.Manifest;

namespace Forge.Core.Catalogue;

/// <summary>What a row's main button does.</summary>
public enum RowAction { None, SignIn, Buy, Install }

/// <summary>
/// The words and the main action of one plugin row in the 0.4.x Hub, as a pure function of what the row knows, so the
/// labels can be tested without a window. A free plugin, and a paid one without a free tier, read exactly as in 0.4.4.
/// A paid plugin with a live free tier that the account does not hold licensed reads "Install Lite", with "Buy Full"
/// beside it.
/// </summary>
public sealed record PluginRowState
{
    /// <summary>The build the row offers: the newest, or a free tier's newest free-tier build. Null: nothing released.</summary>
    public VersionInfo? Offered { get; init; }
    public string? InstalledVersion { get; init; }
    public bool SignedIn { get; init; }
    public bool IsPaid { get; init; }
    /// <summary>The account holds it (any route, a preview claim included).</summary>
    public bool Owned { get; init; }
    public PaidAccess Access { get; init; } = PaidAccess.Free;
    /// <summary>"lite" or "trial" when the offer has a free tier; null otherwise.</summary>
    public string? Tier { get; init; }
    /// <summary>The sentence for a build below the free tier's floor (<see cref="PaidAccess.NeedsLicence"/>).</summary>
    public string? NeedsLicenceLine { get; init; }
    /// <summary>The licence's standing for this plugin, when it is paid.</summary>
    public ProductLicence? Licence { get; init; }

    public bool Released => Offered is not null;
    public bool Installed => InstalledVersion is not null;
    public bool IsFreeTier => Access == PaidAccess.FreeTier;
    public bool TierLive => Tier is not null;
    private string Word => FreeTiers.Word(Tier);

    /// <summary>Paid, and nothing this account may install without buying it.</summary>
    public bool NeedsPurchase => IsPaid && Access is PaidAccess.NotOwned or PaidAccess.NeedsLicence;

    /// <summary>
    /// A different build is offered. A free tier only ever moves forward: a copy newer than its newest free-tier build
    /// (installed while licensed) is not "updated" down to it.
    /// </summary>
    public bool HasUpdate =>
        Installed && Released && InstalledVersion != Offered!.Version
        && (!IsFreeTier || !SemVer.TryParse(InstalledVersion, out var have) || !SemVer.TryParse(Offered.Version, out var offer) || offer > have);

    public string Status =>
        !Released ? "not released"
        : !SignedIn ? "sign in to install"
        : Access == PaidAccess.NeedsLicence ? "paid · needs a licence"
        : NeedsPurchase ? "paid · not on your account"
        : IsFreeTier ? (!Installed ? $"paid · free {(Tier == "trial" ? "trial" : "Lite tier")}" : HasUpdate ? $"{Word} · update available" : $"{Word} · up to date")
        : !Installed ? (Owned ? "on your account" : "not installed")
        : HasUpdate ? "update available"
        : "up to date";

    public string ActionLabel =>
        !Released ? "—"
        : !SignedIn ? "Sign in"
        : NeedsPurchase ? (TierLive ? "Buy Full" : "Buy")
        : IsFreeTier ? (!Installed ? $"Install {Word}" : HasUpdate ? "Update" : "Installed")
        : !Installed ? (Owned ? "Install" : "Add & install")
        : HasUpdate ? "Update"
        : "Installed";

    public RowAction Action =>
        !Released ? RowAction.None
        : !SignedIn ? RowAction.SignIn
        : NeedsPurchase ? RowAction.Buy
        : RowAction.Install;

    public bool CanAct => Released && (!SignedIn || NeedsPurchase || !Installed || HasUpdate);

    /// <summary>"Buy Full" beside the main button: a free-tier row, where the main button installs Lite.</summary>
    public bool CanBuyFull => Released && SignedIn && IsFreeTier;

    /// <summary>The tooltip on the status: the account service's sentence when every build is below the floor.</summary>
    public string? StatusTip => Access == PaidAccess.NeedsLicence ? NeedsLicenceLine : null;

    /// <summary>
    /// The licence line, in Kovati Hub's words (<see cref="LicenceWords.Line"/>): "Licensed · bought 20 Nov 2026",
    /// "Not licensed: this runs as Lite", "Offline, 5 days left", and so on. Empty for a free plugin, and for a paid one
    /// with nothing to say. "Sign in to get Full back" only for a copy that is installed here.
    /// </summary>
    public string LicenceLine
    {
        get
        {
            if (!IsPaid || Licence is not { } l) return "";
            if (l.Standing == LicenceStanding.SignedOut && !Installed) return "";
            // Every build for this engine is below the free tier's floor and none is here: nothing "runs as Lite".
            if (Access == PaidAccess.NeedsLicence && !Installed && !l.CountsAsLicensed) return "";
            return LicenceWords.Line(l, TierLive, Tier == "trial");
        }
    }

    /// <summary>
    /// "Restore licence" beside the line: signed in, a paid plugin with a live free tier, not counted as licensed right
    /// now. It asks Kovati for the licence at once (the same as Refresh), for a purchase the licence does not show yet.
    /// </summary>
    public bool CanRestoreLicence =>
        SignedIn && IsPaid && TierLive && Licence is { Standing: not (LicenceStanding.Licensed or LicenceStanding.SignedOut) };
}
