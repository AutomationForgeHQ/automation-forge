using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Forge.Core.Cloud;
using Forge.Core.Entitlements;

namespace Forge.Core.Licence;

/// <summary>What the screens and the CLI read: the stored licence (verified), the check record, the account, the time.</summary>
public sealed record LicenceSnapshot(VerifiedLicence? Licence, LicenceCheck Check, string? AccountUid, DateTimeOffset Now)
{
    /// <summary>The stored licence when it is for the signed-in account.</summary>
    public LicencePayload? Payload => Licence is { } l && AccountUid is not null && l.Payload.Sub == AccountUid ? l.Payload : null;

    public ProductLicence For(string productId, int? major = null) =>
        LicenceRules.Evaluate(Payload, AccountUid, productId, Now, Check.Sub == AccountUid ? Check.LastOutcome : null, major);

    /// <summary>Licensing is not switched on at the server (or its route does not exist yet).</summary>
    public bool ServerOff => Check.Sub == AccountUid && Check.LastOutcome == LicenceFetchOutcome.Off;
}

/// <summary>
/// Fetches the signed licence for the signed-in account and keeps licence.json in step (step 2 of the licensing plan).
/// When: at sign-in, at start and from the watcher (both only once the last successful fetch is 20 hours old), and
/// always on Check now, Restore licence and `forge licence refresh`. Offline, 429, 5xx, licence-off: the stored file
/// stays as it is and nothing is said. 401/403: the stored file is removed.
/// </summary>
public sealed class LicenceService
{
    private readonly HttpClient _http;
    private readonly Func<StoredAccount?> _account;
    private readonly Func<StoredAccount, CancellationToken, Task<string>> _idToken;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string> _apiUrl;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LicenceService(HttpClient http, Func<StoredAccount?> account, Func<StoredAccount, CancellationToken, Task<string>> idToken,
        LicenceStore? store = null, Func<DateTimeOffset>? clock = null, Func<string>? apiUrl = null)
    {
        _http = http;
        _account = account;
        _idToken = idToken;
        Store = store ?? new LicenceStore();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _apiUrl = apiUrl ?? (() => CloudConfig.ApiUrl);
    }

    /// <summary>For a process that does not hold the window's sign-in (the watcher in the tray, the CLI): the stored sign-in, read each time.</summary>
    public static LicenceService ForStoredAccount(HttpClient http, LicenceStore? store = null)
    {
        var auth = new FirebaseAuth(http);
        return new LicenceService(http, () => CloudConfig.Configured ? AuthState.Load() : null, (a, ct) => auth.IdTokenAsync(a.RefreshToken, ct), store);
    }

    public LicenceStore Store { get; }

    /// <summary>licence.json changed: a newer licence was stored, or it was removed.</summary>
    public event Action? Changed;

    public string RouteUrl => $"{_apiUrl().TrimEnd('/')}/licence";

    public LicenceSnapshot Snapshot()
    {
        var account = _account();
        return new LicenceSnapshot(Store.Load(), Store.LoadCheck(), account?.Uid, _clock());
    }

    /// <summary>
    /// Ask for a licence. <paramref name="force"/>: always (sign-in, Check now, Restore, the CLI); otherwise only when
    /// <see cref="LicenceRules.RefreshDue"/> says so.
    /// </summary>
    public async Task<LicenceFetchOutcome> RefreshAsync(bool force, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var account = _account();
            if (account is null)
            {
                // Signed out: no licence stays behind.
                if (Store.DiscardIfNotFor(null)) Changed?.Invoke();
                return LicenceFetchOutcome.NotSignedIn;
            }
            if (Store.DiscardIfNotFor(account.Uid)) Changed?.Invoke();

            var now = _clock();
            var check = Store.LoadCheck();
            if (check.Sub != account.Uid) check = new LicenceCheck { Sub = account.Uid };
            if (!force && !LicenceRules.RefreshDue(now, check.LastSuccessAt, check.LastServerAnswerAt, check.LastOutcome, Store.Load() is not null))
                return LicenceFetchOutcome.NotDue;

            check.LastAttemptAt = now;
            var outcome = await FetchAsync(account, check, now, ct);
            check.LastOutcome = outcome;
            Store.SaveCheck(check);
            if (outcome is LicenceFetchOutcome.Updated or LicenceFetchOutcome.Refused) Changed?.Invoke();
            return outcome;
        }
        finally { _gate.Release(); }
    }

    private async Task<LicenceFetchOutcome> FetchAsync(StoredAccount account, LicenceCheck check, DateTimeOffset now, CancellationToken ct)
    {
        string token;
        try { token = await _idToken(account, ct); }
        catch (EntitlementException) { return LicenceFetchOutcome.NotSignedIn; } // the sign-in no longer renews: keep the file, it runs out by itself
        catch (HttpRequestException) { return LicenceFetchOutcome.Offline; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return LicenceFetchOutcome.Offline; }

        using var req = new HttpRequestMessage(HttpMethod.Get, RouteUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException) { return LicenceFetchOutcome.Offline; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return LicenceFetchOutcome.Offline; }

        using (resp)
        {
            check.LastServerAnswerAt = now;
            byte[] body;
            try { body = await resp.Content.ReadAsByteArrayAsync(ct); }
            catch (HttpRequestException) { return LicenceFetchOutcome.Offline; }

            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    var save = Store.Offer(body, account.Uid, out _);
                    if (save is LicenceSave.Stored or LicenceSave.NotNewer) check.LastSuccessAt = now;
                    return save switch
                    {
                        LicenceSave.Stored => LicenceFetchOutcome.Updated,
                        LicenceSave.NotNewer => LicenceFetchOutcome.Unchanged,
                        LicenceSave.WriteFailed => LicenceFetchOutcome.WriteFailed,
                        _ => LicenceFetchOutcome.Invalid,
                    };
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    Store.Delete();
                    check.LastSuccessAt = null;
                    return LicenceFetchOutcome.Refused;
                case HttpStatusCode.TooManyRequests:
                    return LicenceFetchOutcome.RateLimited;
                case HttpStatusCode.NotFound:
                    return LicenceFetchOutcome.Off; // the route is not deployed yet: the same as licensing off
                case HttpStatusCode.ServiceUnavailable:
                    return ErrorCode(body) == "licence-off" ? LicenceFetchOutcome.Off : LicenceFetchOutcome.ServerError;
                default:
                    return LicenceFetchOutcome.ServerError;
            }
        }
    }

    /// <summary>The API's short reason: { "code": "licence-off" } or { "error": "licence-off" }.</summary>
    private static string? ErrorCode(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var name in new[] { "code", "error" })
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                    if (v.GetString() == "licence-off") return "licence-off";
        }
        catch (JsonException) { }
        return null;
    }
}
