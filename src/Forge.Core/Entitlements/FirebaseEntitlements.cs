using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Core.Cloud;
using Forge.Core.Manifest;

namespace Forge.Core.Entitlements;

/// <summary>
/// The account-backed provider. The stored sign-in yields an ID token; the
/// person's record (users/{uid}, written only by the backend) lists what the
/// account holds; the account API claims free plugins and issues download
/// URLs. Nothing here writes Firestore — every change goes through the API.
/// </summary>
public sealed class FirebaseEntitlements : IEntitlementProvider
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly FirebaseAuth _auth;
    private StoredAccount? _account;
    private HashSet<string>? _owned;

    public FirebaseEntitlements(HttpClient http)
    {
        _http = http;
        _auth = new FirebaseAuth(http);
        _account = CloudConfig.Configured ? AuthState.Load() : null;
    }

    public bool IsSignedIn => _account is not null;
    public string? AccountLabel => _account?.Email;
    public StoredAccount? Account => _account;

    public void SignIn(StoredAccount account)
    {
        _account = account;
        _owned = null;
        _auth.Forget();
        AuthState.Save(account);
    }

    public void SignOut()
    {
        _account = null;
        _owned = null;
        _auth.Forget();
        AuthState.Clear();
    }

    /// <summary>Forget what was read about ownership; the next question asks again.</summary>
    public void Invalidate() => _owned = null;

    /// <summary>Ask Firebase who this session is now — name, photo, providers — and remember it. Null when signed out or unreachable.</summary>
    public async Task<StoredAccount?> RefreshProfileAsync(CancellationToken ct = default)
    {
        if (_account is null) return null;
        try
        {
            var token = await _auth.IdTokenAsync(_account.RefreshToken, ct);
            var info = await _auth.LookupAsync(token, ct);
            if (info is null) return _account;
            _account = _account with
            {
                Email = info.Email.Length > 0 ? info.Email : _account.Email,
                DisplayName = info.DisplayName ?? _account.DisplayName,
                PhotoUrl = info.PhotoUrl ?? _account.PhotoUrl,
                Providers = info.Providers.ToList(),
            };
            AuthState.Save(_account);
            return _account;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or EntitlementException)
        {
            return _account; // offline, or a session that no longer refreshes: what we remembered stands, and the next real call says why
        }
    }

    /// <summary>Plugin ids on this account, from its record. Empty when signed out or nothing is recorded.</summary>
    public async Task<IReadOnlySet<string>> OwnedAsync(CancellationToken ct = default)
    {
        if (_owned is not null) return _owned;
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_account is null) return owned;

        var token = await _auth.IdTokenAsync(_account.RefreshToken, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{CloudConfig.FirestoreUrl}/projects/{CloudConfig.ProjectId}/databases/(default)/documents/users/{_account.Uid}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return _owned = owned;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("fields", out var fields)
            && fields.TryGetProperty("entitlements", out var ent)
            && ent.TryGetProperty("arrayValue", out var arr)
            && arr.TryGetProperty("values", out var values))
        {
            foreach (var v in values.EnumerateArray())
                if (v.TryGetProperty("stringValue", out var sv) && sv.GetString() is { } id) owned.Add(id);
        }
        return _owned = owned;
    }

    public async Task<bool> OwnsAsync(PluginInfo plugin, CancellationToken ct = default) =>
        _account is not null && (await OwnedAsync(ct)).Contains(plugin.Id);

    public async Task<ClaimResult> ClaimAsync(IEnumerable<string> pluginIds, CancellationToken ct = default)
    {
        var r = await PostAsync<ClaimResponse>("claim", new { plugins = pluginIds.ToArray() }, ct);
        _owned = null; // the record changed
        return new ClaimResult(r.Added, r.AlreadyOwned, r.Paid);
    }

    public async Task<ResolvedDownload> ResolveDownloadAsync(PluginInfo plugin, VersionInfo version, CancellationToken ct = default)
    {
        var body = new { plugin = plugin.Id, version = version.Version, engine = version.Engine, platform = version.Platform, channel = version.Channel };
        try
        {
            return ToResolved(await PostAsync<DownloadResponse>("download", body, ct));
        }
        catch (EntitlementException ex) when (ex.Code == "not-claimed")
        {
            // A free plugin not yet on the account: add it and ask once more. The
            // caller sees the claim in the log line; the person sees "yours" next time.
            var claim = await ClaimAsync([plugin.Id], ct);
            if (!claim.Added.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase) && !claim.AlreadyOwned.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase))
                throw;
            return ToResolved(await PostAsync<DownloadResponse>("download", body, ct));
        }
    }

    private static ResolvedDownload ToResolved(DownloadResponse d) =>
        new(d.Url, d.Sha256, d.Size, DateTimeOffset.TryParse(d.ExpiresAt, out var e) ? e : DateTimeOffset.UtcNow.AddMinutes(5));

    /// <summary>One call to the account API, with the ID token; an error body becomes an EntitlementException carrying its code.</summary>
    private async Task<T> PostAsync<T>(string route, object body, CancellationToken ct)
    {
        if (_account is null) throw new EntitlementException(AnonymousEntitlements.SignInMessage, "unauthenticated");
        var token = await _auth.IdTokenAsync(_account.RefreshToken, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{CloudConfig.ApiUrl}/{route}") { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new EntitlementException($"The account service could not be reached ({ex.Message}).", "unreachable"); }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                ApiError? err = null;
                try { err = JsonSerializer.Deserialize<ApiError>(text, Json); } catch (JsonException) { }
                throw new EntitlementException(err?.Error ?? $"The account service answered {(int)resp.StatusCode}.", err?.Code ?? ((int)resp.StatusCode).ToString());
            }
            return JsonSerializer.Deserialize<T>(text, Json) ?? throw new EntitlementException("The account service answered with nothing.", "empty");
        }
    }

    private sealed record ApiError([property: JsonPropertyName("error")] string? Error, [property: JsonPropertyName("code")] string? Code);
    private sealed record ClaimResponse(
        [property: JsonPropertyName("added")] List<string> Added,
        [property: JsonPropertyName("alreadyOwned")] List<string> AlreadyOwned,
        [property: JsonPropertyName("paid")] List<string> Paid);
    private sealed record DownloadResponse(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("sha256")] string? Sha256,
        [property: JsonPropertyName("size")] long? Size,
        [property: JsonPropertyName("expiresAt")] string? ExpiresAt);
}
