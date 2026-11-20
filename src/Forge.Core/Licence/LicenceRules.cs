using System.Globalization;

namespace Forge.Core.Licence;

/// <summary>Where one paid product stands under the licence on this computer.</summary>
public enum LicenceStanding
{
    /// <summary>The licence lists it and is current.</summary>
    Licensed,
    /// <summary>The licence is current and does not list it (or not for this major version): it runs as its free tier, if it has one.</summary>
    NotLicensed,
    /// <summary>Listed, still valid, but last refreshed more than 23 days ago: "offline, N days left".</summary>
    OfflineGrace,
    /// <summary>Listed, but past its 30 days (or the clock is behind its issue time): the free tier until a refresh.</summary>
    Expired,
    /// <summary>No licence and nothing to worry about: licensing is not on yet on the server, or the first fetch has not happened.</summary>
    NoLicenceYet,
    /// <summary>No account (or the account was refused): the free tier until the person signs in.</summary>
    SignedOut,
}

/// <summary>The result of the last call to the licence route, as the rules and the screens need it.</summary>
public enum LicenceFetchOutcome
{
    /// <summary>A new licence verified, matched the account, was newer, and was stored.</summary>
    Updated,
    /// <summary>A licence verified and matched, but was not newer than the stored one: nothing changed.</summary>
    Unchanged,
    /// <summary>503 licence-off, or the route does not exist yet (404): licensing is not on.</summary>
    Off,
    /// <summary>401 or 403: signed out on the server or the account was deleted. The stored licence was removed.</summary>
    Refused,
    /// <summary>429.</summary>
    RateLimited,
    /// <summary>5xx other than licence-off.</summary>
    ServerError,
    /// <summary>No connection, a timeout, DNS.</summary>
    Offline,
    /// <summary>The answer did not verify, or was for another account. Nothing stored.</summary>
    Invalid,
    /// <summary>A licence verified but could not be written (the file was held open). The stored one stays; the next check tries again.</summary>
    WriteFailed,
    /// <summary>No account on this computer, or its sign-in no longer renews.</summary>
    NotSignedIn,
    /// <summary>Not asked: the last successful fetch is younger than 20 hours.</summary>
    NotDue,
}

/// <summary>One paid product's licence, as decided by <see cref="LicenceRules.Evaluate"/>.</summary>
public sealed record ProductLicence(LicenceStanding Standing)
{
    public string? How { get; init; }
    public bool Test { get; init; }
    public DateOnly? Since { get; init; }
    /// <summary>Whole days left before the licence stops counting (OfflineGrace; 0 once expired).</summary>
    public int DaysLeft { get; init; }
    /// <summary>The computer's clock is earlier than the licence's issue time: it needs a refresh, it is not valid forever.</summary>
    public bool ClockBehind { get; init; }
    public int? MaxMajor { get; init; }
    /// <summary>NotLicensed because the product's major version is above what the licence covers.</summary>
    public bool BeyondMajor { get; init; }

    public bool CountsAsLicensed => Standing is LicenceStanding.Licensed or LicenceStanding.OfflineGrace;
}

/// <summary>
/// The licence rules, as pure functions (LICENSING_STRATEGY decision 4 and KOVATI_HUB_PLAN 5.3): valid 30 days from
/// issue, a quiet line from day 23, refreshed at most once per 20 hours by itself, and a clock earlier than the issue
/// time counts as needing a refresh. Plugins implement the same rules against the same file.
/// </summary>
public static class LicenceRules
{
    public static readonly TimeSpan OfflineWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan GraceAfter = TimeSpan.FromDays(23);
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(20);
    /// <summary>How far the clock may be behind issuedAt before the licence stops counting (clock skew, not tampering).</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(15);
    /// <summary>After the server refused for now (429, 5xx, licence-off), the automatic check waits this long before asking again.</summary>
    public static readonly TimeSpan RetryAfterServerAnswer = TimeSpan.FromHours(1);
    /// <summary>The longest Retry-After honoured (the server's limit is 20 calls per rolling hour per account).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(2);

    /// <summary>
    /// One paid product for the selected account.
    /// </summary>
    /// <param name="licence">The verified payload on disk, or null.</param>
    /// <param name="accountUid">The signed-in account, or null when signed out.</param>
    /// <param name="productId">The plugin id the licence lists it by.</param>
    /// <param name="now">The computer's clock, UTC.</param>
    /// <param name="lastOutcome">The last fetch's outcome, when one is recorded.</param>
    /// <param name="productMajor">The major version in question (installed or offered), when known.</param>
    public static ProductLicence Evaluate(LicencePayload? licence, string? accountUid, string productId, DateTimeOffset now,
        LicenceFetchOutcome? lastOutcome = null, int? productMajor = null)
    {
        if (string.IsNullOrEmpty(accountUid)) return new ProductLicence(LicenceStanding.SignedOut);
        if (licence is null || !string.Equals(licence.Sub, accountUid, StringComparison.Ordinal))
            return new ProductLicence(lastOutcome == LicenceFetchOutcome.Refused ? LicenceStanding.SignedOut : LicenceStanding.NoLicenceYet);

        var p = licence.Product(productId);
        if (p is null) return new ProductLicence(LicenceStanding.NotLicensed);
        var listed = new ProductLicence(LicenceStanding.Licensed)
        {
            How = p.How,
            Test = licence.Test || p.How == "test",
            Since = p.SinceDate,
            MaxMajor = p.MaxMajor,
        };
        if (productMajor is { } major && p.MaxMajor is { } max && major > max)
            return listed with { Standing = LicenceStanding.NotLicensed, BeyondMajor = true };
        if (now < licence.IssuedAt - ClockTolerance)
            return listed with { Standing = LicenceStanding.Expired, ClockBehind = true };
        var until = licence.ValidUntil;
        if (now >= until) return listed with { Standing = LicenceStanding.Expired };
        var daysLeft = (int)Math.Ceiling((until - now).TotalDays);
        if (now - licence.IssuedAt > GraceAfter) return listed with { Standing = LicenceStanding.OfflineGrace, DaysLeft = daysLeft };
        return listed with { DaysLeft = daysLeft };
    }

    /// <summary>
    /// Whether an automatic check (start, the watcher) should ask the server now. Sign-in, Check now, Restore licence
    /// and `forge licence refresh` always ask.
    /// </summary>
    /// <param name="haveLicenceForAccount">A verified licence for this account is on disk.</param>
    public static bool RefreshDue(DateTimeOffset now, DateTimeOffset? lastSuccess, DateTimeOffset? lastServerAnswer, LicenceFetchOutcome? lastOutcome, bool haveLicenceForAccount)
    {
        // A server that said "not now" (429, 5xx, licence-off) is not asked again within the hour, whatever else holds.
        if (lastOutcome is LicenceFetchOutcome.RateLimited or LicenceFetchOutcome.ServerError or LicenceFetchOutcome.Off
            && lastServerAnswer is { } answered && now >= answered && now - answered < RetryAfterServerAnswer)
            return false;
        if (!haveLicenceForAccount) return true;
        if (lastSuccess is not { } last) return true;
        if (now < last) return true; // the clock moved back: the age is unknown
        return now - last >= RefreshAfter;
    }
}

/// <summary>The words for a licence, in the Hub's voice. An empty string means: show nothing.</summary>
public static class LicenceWords
{
    private static string Date(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// The small line on a product page and in Library.
    /// </summary>
    /// <param name="limitedTierLive">The product's free tier (Lite) or trial really exists today. Until it does, nothing
    /// may say the tool is limited: only "Licensed" lines are shown.</param>
    /// <param name="trial">The product's free form is a trial (HairForge), not Lite.</param>
    public static string Line(ProductLicence l, bool limitedTierLive, bool trial = false)
    {
        var free = trial ? "a trial" : "Lite";
        switch (l.Standing)
        {
            case LicenceStanding.Licensed:
                return Licensed(l);
            case LicenceStanding.OfflineGrace:
                return limitedTierLive ? $"Offline, {Days(l.DaysLeft)} left" : Licensed(l);
            case LicenceStanding.Expired:
                if (!limitedTierLive) return "";
                return l.ClockBehind
                    ? "This computer's clock is behind. Set the date, then Restore licence"
                    : $"Running as {free} until you are online";
            case LicenceStanding.NotLicensed:
                if (!limitedTierLive) return "";
                return l.BeyondMajor && l.MaxMajor is { } m
                    ? $"Your licence covers version {m}. This runs as {free}"
                    : $"Not licensed: this runs as {free}";
            case LicenceStanding.SignedOut:
                return limitedTierLive ? "Sign in to get Full back" : "";
            default:
                return "";
        }
    }

    private static string Licensed(ProductLicence l)
    {
        if (l.Test) return "Test licence";
        return l.How switch
        {
            "bought" when l.Since is { } d => $"Licensed · bought {Date(d)}",
            "granted" when l.Since is { } d => $"Licensed · granted {Date(d)}",
            "fab" => "Licensed · from Fab",
            "pro" => "Licensed · through Pro",
            _ => "Licensed",
        };
    }

    private static string Days(int n) => n == 1 ? "1 day" : $"{n} days";

    /// <summary>Settings' one explanatory line.</summary>
    public const string SettingsLine = "Paid tools check this file. It is refreshed once a day and stays valid for 30 days offline.";
}
