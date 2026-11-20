// licence-fake-server: the account API's licence route, faked locally, signed with a THROWAWAY key made at start.
//
//   dotnet run --project tools/licence-fake-server -- --port 8791 --data D:\UNREAL\ColonyDownloads\hub-lictest-data [--mode ok]
//
// --data writes a fake sign-in (account.bin) into that folder so a Hub or forge pointed at this server is "signed in".
// It refuses the real %LOCALAPPDATA%\AutomationForge. It prints the environment a Hub needs:
//   FORGE_HUB_INSTANCE, FORGE_DATA_DIR, FORGE_LICENCE_DEV_KEYS, FORGE_API_URL, FORGE_FIREBASE_EMULATOR, FORGE_FIRESTORE_EMULATOR
//
// Modes (also switchable while running: GET /_mode/<mode>):
//   ok        TerrainForge bought, PerformanceForge test; issued now
//   empty     a valid licence that lists nothing
//   grace     issued 25 days ago        expired   issued 33 days ago
//   other     a licence for another account (must be discarded)
//   badsig    signed by a different throwaway key (must not verify)
//   off       503 { code: "licence-off" }   401   429 (Retry-After: 120)   500
//
// Nothing here is trusted by a release build: the key is made per run, and dev keys are read only by Debug builds or
// under FORGE_HUB_INSTANCE, never for the kid "k1".

using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Core.Cloud;
using Forge.Licence.Testing;

string Arg(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }

var port = int.Parse(Arg("--port", "8791"));
var data = Arg("--data", "");
var mode = Arg("--mode", "ok");
const string Uid = "0123456789abcdef0123456789abcdef";
const string OtherUid = "fedcba9876543210fedcba9876543210";
const string Email = "lictest@kovati.dev";
var Sub = Forge.Core.Licence.LicencePayload.SubjectFor(Uid);
var OtherSub = Forge.Core.Licence.LicencePayload.SubjectFor(OtherUid);

var key = TestLicenceKey.Generate("dev1");
var stranger = TestLicenceKey.Generate("dev1");

if (data.Length > 0)
{
    var real = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutomationForge"));
    if (string.Equals(Path.GetFullPath(data).TrimEnd('\\'), real.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Refusing to write a fake sign-in into the real data folder.");
        return 2;
    }
    Directory.CreateDirectory(data);
    Environment.SetEnvironmentVariable("FORGE_DATA_DIR", data);
    AuthState.Save(new StoredAccount(Uid, Email, "fake-refresh-token") { DisplayName = "Licence Test" });
    Console.WriteLine($"fake sign-in written to {Path.Combine(data, "account.bin")}");
}

var host = $"127.0.0.1:{port}";
Console.WriteLine($"""
    $env:FORGE_HUB_INSTANCE = 'lictest'
    $env:FORGE_DATA_DIR = '{data}'
    $env:FORGE_LICENCE_DEV_KEYS = '{key.DevKeysValue}'
    $env:FORGE_API_URL = 'http://{host}/api'
    $env:FORGE_FIREBASE_EMULATOR = '{host}'
    $env:FORGE_FIRESTORE_EMULATOR = '{host}'
    mode: {mode}
    """);

var listener = new HttpListener();
listener.Prefixes.Add($"http://{host}/");
listener.Start();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; listener.Stop(); };

static string T(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

while (listener.IsListening)
{
    HttpListenerContext ctx;
    try { ctx = await listener.GetContextAsync(); }
    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
    var path = ctx.Request.Url!.AbsolutePath;
    int status = 200;
    string body;
    var now = DateTimeOffset.UtcNow;
    var products = new Dictionary<string, (string how, string since, int maxMajor)>
    {
        ["TerrainForge"] = ("bought", T(now.AddDays(-3)), 1),
        ["PerformanceForge"] = ("test", T(now.AddDays(-1)), 1),
    };

    if (path.StartsWith("/_mode/")) { mode = path["/_mode/".Length..]; body = $$"""{ "mode": "{{mode}}" }"""; }
    else if (path.EndsWith("/securetoken.googleapis.com/v1/token"))
        body = $$"""{ "id_token": "fake-id-token-{{Uid}}", "refresh_token": "fake-refresh-token", "expires_in": "3600", "user_id": "{{Uid}}" }""";
    else if (path.EndsWith("/identitytoolkit.googleapis.com/v1/accounts:lookup"))
        body = $$"""{ "users": [ { "localId": "{{Uid}}", "email": "{{Email}}", "displayName": "Licence Test", "providerUserInfo": [ { "providerId": "google.com" } ] } ] }""";
    else if (path.Contains("/documents/users/"))
        body = """{ "fields": { "entitlements": { "arrayValue": { "values": [ { "stringValue": "TerrainForge" }, { "stringValue": "PerformanceForge" } ] } } } }""";
    else if (path == "/api/licence/keys")
        body = JsonSerializer.Serialize(new { keys = new[] { new { kid = key.Kid, alg = "Ed25519", publicKey = key.PublicKeyBase64Url } } });
    else if (path == "/api/licence")
    {
        var auth = ctx.Request.Headers["Authorization"] ?? "";
        if (!auth.StartsWith("Bearer fake-id-token-")) { status = 401; body = """{ "error": "Sign in again.", "code": "unauthenticated" }"""; }
        else switch (mode)
        {
            case "ok": body = key.Envelope(Sub, now, products); break;
            case "empty": body = key.Envelope(Sub, now); break;
            case "grace": body = key.Envelope(Sub, now.AddDays(-25), products); break;
            case "expired": body = key.Envelope(Sub, now.AddDays(-33), products); break;
            case "other": body = key.Envelope(OtherSub, now, products); break;
            case "badsig": body = stranger.Envelope(Sub, now, products); break;
            case "off": status = 503; body = """{ "error": "Licences are not configured.", "code": "licence-off" }"""; break;
            case "401": status = 401; body = """{ "error": "Sign in again.", "code": "unauthenticated" }"""; break;
            case "429": status = 429; ctx.Response.AddHeader("Retry-After", "120"); body = """{ "error": "Too many requests.", "code": "rate-limited" }"""; break;
            default: status = 500; body = """{ "error": "boom" }"""; break;
        }
    }
    else { status = 404; body = """{ "error": "not here" }"""; }

    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {ctx.Request.HttpMethod} {path} -> {status} ({mode})");
    var bytes = Encoding.UTF8.GetBytes(body);
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes);
    ctx.Response.Close();
}
return 0;
