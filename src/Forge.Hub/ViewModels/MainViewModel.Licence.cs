using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Catalogue;
using Forge.Core.Cloud;
using Forge.Core.Licence;

namespace Forge.Hub.ViewModels;

/// <summary>
/// The signed licence (licensing plan, step 2): fetched at sign-in, at start and once a day (the 20-hour rule), and on
/// Check now and Restore licence; shown quietly on paid products' pages, in Library and in Settings. Nothing here
/// says a tool is limited while no plugin enforces a limit: the Lite and trial words wait on the product's free tier
/// being live in the catalogue.
/// </summary>
public partial class MainViewModel
{
    private LicenceService? _licence;
    private LicenceSnapshot? _licenceSnapshot;
    private string? _previewUid;

    [ObservableProperty] private bool _restoringLicence;

    /// <summary>The service over this window's sign-in (the preview account in the screenshot harness).</summary>
    internal LicenceService Licence => _licence ??= CreateLicenceService();

    private LicenceService CreateLicenceService()
    {
        var service = new LicenceService(_http,
            () => _previewSignedIn ? (_previewUid is { } uid ? new StoredAccount(uid, AccountEmail, "") : null) : _entitlements.Account,
            (_, ct) => _entitlements.IdTokenAsync(ct));
        service.Changed += () => Dispatcher.UIThread.Post(LicenceChanged);
        return service;
    }

    /// <summary>
    /// The licence as last read from disk. Read again when it changes here, when the watcher says so, and when the
    /// file's time differs from the one read (`forge licence refresh` run by a plugin while the window is open).
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

    private string? _licenceStamp;

    private static string FileStamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture) : "-"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "?"; }
    }

    /// <summary>licence.json changed (here, or from the tray's watcher): read it again and redraw.</summary>
    public void LicenceChanged()
    {
        _licenceSnapshot = null;
        AnnounceLicence();
        CurrentPage?.Refresh();
    }

    /// <summary>
    /// Fetch the licence. <paramref name="force"/> for sign-in, Check now and Restore licence; otherwise the 20-hour rule.
    /// Offline or refused for now: nothing is said unless the person asked.
    /// </summary>
    internal async Task<LicenceFetchOutcome> RefreshLicenceAsync(bool force, bool say = false)
    {
        LicenceFetchOutcome outcome;
        try { outcome = await Licence.RefreshAsync(force); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { outcome = LicenceFetchOutcome.Offline; }
        _licenceSnapshot = null;
        AnnounceLicence();
        CurrentPage?.Refresh();
        if (say) Say(outcome switch
        {
            LicenceFetchOutcome.Updated or LicenceFetchOutcome.Unchanged => "Licence refreshed.",
            LicenceFetchOutcome.Off => "Licences are not switched on at Kovati yet. Nothing changed.",
            LicenceFetchOutcome.Refused => "Kovati did not accept this sign-in. Sign in again.",
            LicenceFetchOutcome.NotSignedIn => "Sign in to get your licence.",
            LicenceFetchOutcome.Invalid => "The licence from Kovati did not check out, so the one on this computer stays.",
            LicenceFetchOutcome.WriteFailed => "The licence file is in use by another program. Try again in a moment.",
            _ => "Could not reach Kovati. Your licence keeps working.",
        });
        return outcome;
    }

    /// <summary>"Restore licence": just the refresh, always asked.</summary>
    [RelayCommand]
    private async Task RestoreLicenceAsync()
    {
        if (RestoringLicence) return;
        RestoringLicence = true;
        try { await RefreshLicenceAsync(force: true, say: true); }
        finally { RestoringLicence = false; }
    }

    partial void OnRestoringLicenceChanged(bool value) => OnPropertyChanged(nameof(RestoreLicenceLabel));

    public string RestoreLicenceLabel => RestoringLicence ? "Restoring…" : "Restore licence";

    /// <summary>The id a licence lists a product by: its first component, when the manifest says that component is paid.</summary>
    public string? LicenceKeyOf(Product product) =>
        product.MainComponent is { } main && _manifest?.Plugin(main) is { IsPaid: true } ? main : null;

    /// <summary>The product's Lite or trial exists today. Until then nothing may say the tool is limited.</summary>
    public static bool LimitedTierLive(Product product) =>
        product.FreeTierLive == true && (product.Price == "free-tier" || product.Trial);

    /// <summary>The product's licence, or null for a product that is not paid.</summary>
    public ProductLicence? LicenceOf(Product product)
    {
        if (LicenceKeyOf(product) is not { } key) return null;
        var installed = InstalledOnTarget().TryGetValue(key, out var v) ? v : null;
        var version = installed ?? _manifest?.Plugin(key)?.Latest(EngineVersion, Channel)?.Version;
        int? major = version is not null && SemVer.TryParse(version, out var sv) ? sv.Major : null;
        return LicenceSnapshot.For(key, major);
    }

    /// <summary>The small line for a product: "Licensed · bought 20 Nov 2026", "Test licence", or "" for nothing.</summary>
    public string LicenceLineOf(Product product) =>
        LicenceOf(product) is { } l ? LicenceWords.Line(l, LimitedTierLive(product), product.Trial && product.Price != "free-tier") : "";

    /// <summary>Restore licence belongs beside a paid product whenever someone is signed in.</summary>
    public bool CanRestoreLicenceFor(Product product) => IsSignedIn && LicenceKeyOf(product) is not null;

    // ── Settings: LICENCE ────────────────────────────────────────────────

    public bool HasLicenceSection => IsSignedIn;
    public bool HasLicence => LicenceSnapshot.Payload is not null;
    public string LicenceSettingsLine => LicenceWords.SettingsLine;
    public string LicenceFilePath => Paths.LicenceFile;

    private static string When(DateTimeOffset t) => t.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    public string LicenceAccountLine => LicenceSnapshot.Payload is null ? "" : $"For {AccountEmail}";

    public string LicenceRefreshedLine => LicenceSnapshot.Payload is not { } p ? ""
        : $"Last refreshed {When(LicenceSnapshot.Check.Sub == p.Sub && LicenceSnapshot.Check.LastSuccessAt is { } at ? at : p.IssuedAt)}";

    public string LicenceValidLine => LicenceSnapshot.Payload is not { } p ? "" : $"Valid offline until {When(p.ValidUntil)}";

    public string LicenceKeyLine => LicenceSnapshot.Licence is not { } l || LicenceSnapshot.Payload is null ? ""
        : $"Signed with key {l.Envelope.Kid}{(l.SignedWithDevKey ? " (a development key)" : "")}{(l.Payload.Test ? " · test licence" : "")}";

    public string LicenceCoversLine => LicenceSnapshot.Payload is not { } p ? ""
        : p.Products.Count == 0 ? "It covers no paid tools yet."
        : $"It covers {string.Join(", ", p.Products.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(NameOfComponent))}.";

    public string NoLicenceLine => "No licence on this computer yet.";

    private string NameOfComponent(string id) => Catalogue?.ProductOfComponent(id)?.Name ?? id;

    private void AnnounceLicence()
    {
        foreach (var name in new[] { nameof(HasLicenceSection), nameof(HasLicence), nameof(LicenceAccountLine), nameof(LicenceRefreshedLine),
                     nameof(LicenceValidLine), nameof(LicenceKeyLine), nameof(LicenceCoversLine), nameof(LicenceSnapshot) })
            OnPropertyChanged(name);
    }

    /// <summary>For the screenshot harness: the preview account's uid, so a licence written for it shows.</summary>
    internal void PreviewLicenceAccount(string uid)
    {
        _previewUid = uid;
        LicenceChanged();
    }
}
