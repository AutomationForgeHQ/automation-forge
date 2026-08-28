using System.Text.Json;
using Forge.Core.Entitlements;

namespace Forge.Core.Cloud;

/// <summary>
/// Firebase Auth over REST: a stored refresh token becomes an hour's ID token
/// on demand, and an ID token names its account. No SDK — two endpoints.
/// </summary>
public sealed class FirebaseAuth(HttpClient http)
{
    private string? _idToken;
    private DateTimeOffset _expires;

    public async Task<string> IdTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        if (_idToken is not null && DateTimeOffset.UtcNow < _expires - TimeSpan.FromMinutes(2)) return _idToken;
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        });
        using var resp = await http.PostAsync($"https://securetoken.googleapis.com/v1/token?key={CloudConfig.ApiKey}", content, ct);
        if (!resp.IsSuccessStatusCode)
            throw new EntitlementException("Your sign-in has expired or was revoked. Sign in again.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        _idToken = doc.RootElement.GetProperty("id_token").GetString()!;
        var seconds = int.TryParse(doc.RootElement.GetProperty("expires_in").GetString(), out var s) ? s : 3600;
        _expires = DateTimeOffset.UtcNow.AddSeconds(seconds);
        return _idToken;
    }

    public sealed record AccountInfo(string Uid, string Email, IReadOnlyList<string> Providers);

    public async Task<AccountInfo?> LookupAsync(string idToken, CancellationToken ct = default)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { idToken }), System.Text.Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync($"https://identitytoolkit.googleapis.com/v1/accounts:lookup?key={CloudConfig.ApiKey}", content, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("users", out var users) || users.GetArrayLength() == 0) return null;
        var u = users[0];
        var providers = new List<string>();
        if (u.TryGetProperty("providerUserInfo", out var infos))
            foreach (var i in infos.EnumerateArray())
                if (i.TryGetProperty("providerId", out var id) && id.GetString() is { } p) providers.Add(p);
        return new AccountInfo(u.GetProperty("localId").GetString() ?? "", u.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "", providers);
    }

    public void Forget() { _idToken = null; }
}
