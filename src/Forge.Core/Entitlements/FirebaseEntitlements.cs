using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Forge.Core.Cloud;
using Forge.Core.Manifest;

namespace Forge.Core.Entitlements;

/// <summary>
/// The account-backed provider. The stored sign-in yields an ID token; the
/// person's Firestore record (users/{uid}, written only by the backend) lists
/// what they own. Paid downloads still wait for the delivery function — until
/// then an owned paid plugin is recognised but not yet fetched.
/// </summary>
public sealed class FirebaseEntitlements : IEntitlementProvider
{
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

    /// <summary>Plugin ids this account owns, from its record. Empty when signed out or nothing is recorded.</summary>
    public async Task<IReadOnlySet<string>> OwnedAsync(CancellationToken ct = default)
    {
        if (_owned is not null) return _owned;
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_account is null) return owned;

        var token = await _auth.IdTokenAsync(_account.RefreshToken, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://firestore.googleapis.com/v1/projects/{CloudConfig.ProjectId}/databases/(default)/documents/users/{_account.Uid}");
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
        !plugin.IsPaid || (await OwnedAsync(ct)).Contains(plugin.Id);

    public Task<string?> ResolveDownloadUrlAsync(PluginInfo plugin, VersionInfo version, CancellationToken ct = default) =>
        Task.FromResult<string?>(null); // the signed-URL function arrives with the paid backend
}
