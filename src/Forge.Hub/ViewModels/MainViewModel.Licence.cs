using System.Globalization;
using Avalonia.Threading;
using Forge.Core;
using Forge.Core.Catalogue;
using Forge.Core.Cloud;
using Forge.Core.Installs;
using Forge.Core.Licence;
using Forge.Core.Manifest;

namespace Forge.Hub.ViewModels;

/// <summary>
/// The signed licence and the free tier of a paid plugin, as in Kovati Hub 0.5.0-beta.7 (its MainViewModel.Licence.cs),
/// cut down to what the 0.4.x window has: one line per paid plugin and its Install Lite / Buy Full buttons.
///
/// The licence (%LOCALAPPDATA%\AutomationForge\licence.json, shared with Kovati Hub) is fetched at sign-in, at start and
/// from the tray's watcher once the last good fetch is 20 hours old, and always on Refresh, the tray's "Check for
/// updates now", Settings' "Check now", Restore licence, and right after a purchase shows up on the account.
/// </summary>
public partial class MainViewModel
{
    private LicenceService? _licence;
    private LicenceSnapshot? _licenceSnapshot;
    private string? _licenceStamp;

    /// <summary>GET me `licensed`: the paid plugins held by a licensed route. Null when the account service did not say.</summary>
    private IReadOnlySet<string>? _licensed;

    /// <summary>GET catalogue: each paid plugin's free tier and which builds it serves. Null until read (kept a day).</summary>
    private AccountCatalogue? _offers;

    /// <summary>The account these holdings were read for, so a purchase is told apart from a first read.</summary>
    private string? _heldFor;

    /// <summary>The service over this window's sign-in.</summary>
    internal LicenceService Licence => _licence ??= CreateLicenceService();

    private LicenceService CreateLicenceService()
    {
        var service = new LicenceService(_http, () => _entitlements.Account, (_, ct) => _entitlements.IdTokenAsync(ct));
        service.Changed += () => Dispatcher.UIThread.Post(LicenceChanged);
        return service;
    }

    /// <summary>
    /// The licence as last read from disk. Read again when it changes here, when the watcher says so, and when the file's
    /// time differs from the one read (Kovati Hub or `forge licence refresh`, run by a plugin, wrote it meanwhile).
    /// </summary>
    public LicenceSnapshot LicenceSnapshot
    {
        get
        {
            var stamp = FileStamp(Licence.Store.FilePath) + "|" + FileStamp(Licence.Store.CheckPath);
            if (_licenceSnapshot is null || stamp != _licenceStamp)
            {
                _licenceSnapshot = Licence.Snapshot();
                _licenceStamp = stamp;
            }
            return _licenceSnapshot;
        }
    }

    private static string FileStamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture) : "-"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "?"; }
    }

    /// <summary>licence.json changed (here, or from the tray's watcher): read it again and redraw the rows.</summary>
    public void LicenceChanged()
    {
        _licenceSnapshot = null;
        // A purchase the licence now lists makes a Lite install Full, with no reinstall.
        Rebuild();
    }

    /// <summary>
    /// Fetch the licence. <paramref name="force"/> for sign-in, Refresh, Check now, Restore licence and a purchase;
    /// otherwise the 20-hour rule. Offline or refused for now: nothing is said unless the person asked.
    /// </summary>
    internal async Task<LicenceFetchOutcome> RefreshLicenceAsync(bool force, bool say = false)
    {
        if (!_entitlements.IsSignedIn) return LicenceFetchOutcome.NotSignedIn;
        LicenceFetchOutcome outcome;
        try { outcome = await Licence.RefreshAsync(force); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { outcome = LicenceFetchOutcome.Offline; }
        _licenceSnapshot = null;
        Rebuild();
        if (say) Say(outcome switch
        {
            LicenceFetchOutcome.Updated or LicenceFetchOutcome.Unchanged => "Licence refreshed.",
            LicenceFetchOutcome.Off => "Licences are not switched on at Kovati yet. Nothing changed.",
            LicenceFetchOutcome.Refused => "Kovati did not accept this sign-in. Sign in again.",
            LicenceFetchOutcome.NotSignedIn => "Sign in to get your licence.",
            LicenceFetchOutcome.Invalid => "The licence from Kovati did not check out, so the one on this computer stays.",
            LicenceFetchOutcome.WriteFailed => "The licence file is in use by another program. Try again in a moment.",
            LicenceFetchOutcome.RateLimited => "Kovati was asked too often for the licence. Try again later; your licence keeps working.",
            _ => "Could not reach Kovati. Your licence keeps working.",
        });
        return outcome;
    }

    /// <summary>"Restore licence" on a row: the refresh, always asked, said out loud.</summary>
    internal Task RestoreLicenceAsync() => RefreshLicenceAsync(force: true, say: true);

    /// <summary>
    /// What the account holds and how (GET me `licensed`), and the offers (GET catalogue). <paramref name="asked"/>: the
    /// person pressed Refresh, so the offers are read again rather than from the day's cache. Returns true when a paid
    /// plugin is held now that was not at the last read for this account: a purchase (or a gift) to fetch the licence for.
    /// </summary>
    private async Task<bool> LoadHoldingsAsync(IReadOnlySet<string> owned, bool asked)
    {
        var uid = _entitlements.Account?.Uid;
        var before = uid is not null && uid == _heldFor ? PaidHeld(_owned, _licensed) : null;
        _licensed = uid is null ? null : await _entitlements.LicensedAsync();
        _offers = await AccountCatalogue.GetAsync(_http, force: asked) ?? _offers;
        _heldFor = uid;
        if (before is null) return false;
        return PaidHeld(owned, _licensed).Except(before, StringComparer.OrdinalIgnoreCase).Any();
    }

    /// <summary>Read the offers when nobody is signed in (GET catalogue needs no account), so a row can say a free tier exists.</summary>
    private async Task LoadOffersAsync(bool asked) => _offers = await AccountCatalogue.GetAsync(_http, force: asked) ?? _offers;

    /// <summary>The paid plugins the account holds, and those it holds licensed, as one set of "id" and "id:licensed".</summary>
    private HashSet<string> PaidHeld(IReadOnlySet<string> owned, IReadOnlySet<string>? licensed)
    {
        var paid = _manifest?.Plugins.Where(p => p.IsPaid).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                   ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in owned) if (paid.Count == 0 || paid.Contains(id)) set.Add(id);
        foreach (var id in licensed ?? Enumerable.Empty<string>()) set.Add(id + ":licensed");
        return set;
    }

    /// <summary>What the free-tier rules read for the selected engine.</summary>
    private AccessInputs? Access() => SelectedEngine is null ? null : new AccessInputs
    {
        Engine = SelectedEngine.Version,
        Channel = Channel,
        Owned = _owned,
        Licensed = _entitlements.IsSignedIn ? FreeTiers.LicensedNow(_licensed, LicenceSnapshot) : null,
        Offers = _offers,
    };

    /// <summary>One row's state: its access, the build it offers, its free tier and its licence.</summary>
    private PluginRowState RowState(PluginInfo p, InstalledPlugin? installed, AccessInputs access)
    {
        var how = FreeTiers.AccessOf(p, access);
        var latest = p.Latest(access.Engine, access.Channel);
        // A row always shows a version when one is published: a build it may not install is offered for Buy, as in 0.4.4.
        var offered = FreeTiers.BuildFor(p, access) ?? latest;
        var tier = p.IsPaid ? FreeTiers.TierOf(p.Id, access) : null;
        int? major = (installed?.Version ?? offered?.Version) is { } v && SemVer.TryParse(v, out var sv) ? sv.Major : null;
        return new PluginRowState
        {
            Offered = offered,
            InstalledVersion = installed?.Version,
            SignedIn = _entitlements.IsSignedIn,
            IsPaid = p.IsPaid,
            Owned = _owned.Contains(p.Id),
            Access = how,
            Tier = tier,
            NeedsLicenceLine = how == PaidAccess.NeedsLicence
                ? $"{FreeTiers.NeedsLicenceLine(p.Id, latest?.Version, tier, FreeTiers.FloorOf(p.Id, access))} Buy it at {CloudConfig.PluginsUrl}"
                : null,
            Licence = p.IsPaid ? LicenceSnapshot.For(p.Id, major) : null,
        };
    }

    /// <summary>
    /// The build of a plugin this account may install on the selected engine: the newest, or its newest free-tier
    /// build. A build it may not have at all is still asked for, so the account service says why in its own words
    /// (a 403 needs-licence: "Version 0.1.0 of TerrainForge needs a licence. The free Lite tier starts with 0.2.0.").
    /// </summary>
    internal VersionInfo? BuildToInstall(PluginInfo plugin)
    {
        if (SelectedEngine is null) return null;
        return (Access() is { } access ? FreeTiers.BuildFor(plugin, access) : null) ?? plugin.Latest(SelectedEngine.Version, Channel);
    }

    /// <summary>The ids for the elevated helper, each as id@version when the build to install is not the newest (a free tier's).</summary>
    private string[] WithVersions(string[] ids) => _manifest is null || SelectedEngine is null ? ids : ids.Select(id =>
        _manifest.Plugin(id) is { } p && BuildToInstall(p) is { } v && p.Latest(SelectedEngine.Version, Channel) is { } latest && latest.Version != v.Version
            ? $"{id}@{v.Version}" : id).ToArray();
}
