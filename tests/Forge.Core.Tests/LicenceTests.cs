using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Core.Cloud;
using Forge.Core.Entitlements;
using Forge.Core.Licence;
using Forge.Licence.Testing;
using Xunit;

namespace Forge.Core.Tests;

/// <summary>Step 2 of the licensing plan: verify, store, refresh and read the signed licence.</summary>
public sealed class LicenceTests : IDisposable
{
    private const string Uid = "0123456789abcdef0123456789abcdef";
    private const string OtherUid = "fedcba9876543210fedcba9876543210";
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, (string how, string since, int maxMajor)> Bought = new()
    {
        ["TerrainForge"] = ("bought", "2026-11-20", 1),
    };

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("forge-licence-tests-");
    private readonly TestLicenceKey _key = TestLicenceKey.Generate("dev1");

    public void Dispose() { try { _dir.Delete(true); } catch (IOException) { } }

    private LicenceStore Store(LicenceKeyRing? keys = null, bool isDefault = false) => new(_dir.FullName, keys ?? _key.Ring(), isDefault);

    // ── verification ─────────────────────────────────────────────────────

    [Fact]
    public void Rfc8032_test_vector_1_verifies_and_a_changed_message_does_not()
    {
        static byte[] Hex(string h) => Convert.FromHexString(h);
        var pk = Hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var sig = Hex("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        Assert.True(LicenceVerifier.Ed25519Verify(pk, [], sig));
        Assert.False(LicenceVerifier.Ed25519Verify(pk, [0], sig));
        // And the throwaway signer agrees with it.
        var seeded = TestLicenceKey.FromSeed("t", Hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"));
        Assert.Equal(pk, seeded.PublicKey);
        Assert.Equal(sig, seeded.SignBytes([]));
    }

    [Fact]
    public void A_good_licence_verifies_and_reads_its_payload()
    {
        var env = _key.Envelope(Uid, T0, Bought);
        var v = LicenceVerifier.Verify(env, _key.Ring(), out var error);
        Assert.Null(error);
        Assert.NotNull(v);
        Assert.Equal(Uid, v.Payload.Sub);
        Assert.Equal(T0, v.Payload.IssuedAt);
        Assert.Equal(T0.AddDays(30), v.Payload.OfflineUntil);
        Assert.Equal("bought", v.Payload.Product("terrainforge")!.How); // ids compare without case
        Assert.Equal(new DateOnly(2026, 11, 20), v.Payload.Product("TerrainForge")!.SinceDate);
        Assert.True(v.SignedWithDevKey);
    }

    [Fact]
    public void A_signature_from_another_key_does_not_verify()
    {
        var other = TestLicenceKey.Generate("dev1");
        Assert.Null(LicenceVerifier.Verify(other.Envelope(Uid, T0, Bought), _key.Ring(), out var error));
        Assert.Contains("does not verify", error);
    }

    [Fact]
    public void A_flipped_signature_bit_does_not_verify()
    {
        var env = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(_key.Envelope(Uid, T0, Bought))!;
        var sig = Base64Url.TryDecode(env["sig"].GetString())!;
        sig[10] ^= 1;
        var tampered = JsonSerializer.Serialize(new { format = "kovati-licence", v = 1, kid = "dev1", payload = env["payload"].GetString(), sig = Base64Url.Encode(sig) });
        Assert.Null(LicenceVerifier.Verify(tampered, _key.Ring(), out _));
    }

    [Fact]
    public void A_payload_changed_by_one_byte_does_not_verify()
    {
        var env = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(_key.Envelope(Uid, T0, Bought))!;
        var bytes = Base64Url.TryDecode(env["payload"].GetString())!;
        var text = Encoding.UTF8.GetString(bytes).Replace("\"maxMajor\":1", "\"maxMajor\":9");
        Assert.NotEqual(Encoding.UTF8.GetString(bytes), text);
        var tampered = JsonSerializer.Serialize(new { format = "kovati-licence", v = 1, kid = "dev1", payload = Base64Url.EncodeUtf8(text), sig = env["sig"].GetString() });
        Assert.Null(LicenceVerifier.Verify(tampered, _key.Ring(), out _));

        // Even a change JSON would not notice: one more space.
        var spaced = JsonSerializer.Serialize(new { format = "kovati-licence", v = 1, kid = "dev1", payload = Base64Url.Encode([.. bytes, (byte)' ']), sig = env["sig"].GetString() });
        Assert.Null(LicenceVerifier.Verify(spaced, _key.Ring(), out _));
    }

    [Fact]
    public void An_unknown_kid_verifies_nothing()
    {
        Assert.Null(LicenceVerifier.Verify(_key.Envelope(TestLicenceKey.Payload(Uid, T0), kid: "k9"), _key.Ring(), out var error));
        Assert.Contains("no usable key", error);
    }

    [Fact]
    public void The_placeholder_key_verifies_nothing_never_everything()
    {
        Assert.Null(LicenceKeys.Usable(LicenceKeys.Placeholder));
        Assert.Null(LicenceKeys.Usable(""));
        Assert.Null(LicenceKeys.Usable("short"));
        Assert.Null(LicenceKeys.Usable(Base64Url.Encode(new byte[32]))); // all zero
        Assert.Null(LicenceKeys.Usable(Base64Url.Encode(new byte[33])));
        // The table as shipped today: k1 is the placeholder, so a licence that claims k1 fails, whatever its signature.
        Assert.Equal(LicenceKeys.Placeholder, LicenceKeys.K1);
        var ring = new LicenceKeyRing(LicenceKeys.BuiltIn);
        Assert.Null(ring.Find("k1"));
        Assert.Null(LicenceVerifier.Verify(_key.Envelope(TestLicenceKey.Payload(Uid, T0), kid: "k1"), ring, out _));
        // A placeholder in a table that also holds a real key: the real kid works, the placeholder kid does not.
        var mixed = new LicenceKeyRing(new Dictionary<string, string> { ["k1"] = LicenceKeys.Placeholder, ["dev1"] = _key.PublicKeyBase64Url });
        Assert.NotNull(LicenceVerifier.Verify(_key.Envelope(TestLicenceKey.Payload(Uid, T0)), mixed, out _));
        Assert.Null(LicenceVerifier.Verify(_key.Envelope(TestLicenceKey.Payload(Uid, T0), kid: "k1"), mixed, out _));
    }

    [Fact]
    public void Dev_keys_come_from_the_environment_in_a_debug_build_and_never_replace_k1()
    {
        var before = Environment.GetEnvironmentVariable(LicenceKeys.DevKeysVariable);
        try
        {
            Environment.SetEnvironmentVariable(LicenceKeys.DevKeysVariable, $"k1={_key.PublicKeyBase64Url}; {_key.DevKeysValue} ;junk;=x");
            var dev = LicenceKeys.DevKeys();
#if DEBUG
            Assert.True(LicenceKeys.DevKeysAllowed);
            Assert.Equal([_key.Kid], dev.Keys);
            Assert.NotNull(LicenceKeyRing.Default().Find("dev1"));
#else
            Assert.Equal(Environment.GetEnvironmentVariable("FORGE_HUB_INSTANCE") is { Length: > 0 }, LicenceKeys.DevKeysAllowed);
#endif
            Assert.False(dev.ContainsKey("k1"));
            Assert.Null(LicenceKeyRing.Default().Find("k1"));
        }
        finally { Environment.SetEnvironmentVariable(LicenceKeys.DevKeysVariable, before); }
    }

    [Theory]
    [InlineData("""{ "format": "other", "v": 1, "kid": "dev1", "payload": "e30", "sig": "AA" }""")]
    [InlineData("""{ "format": "kovati-licence", "v": 2, "kid": "dev1", "payload": "e30", "sig": "AA" }""")]
    [InlineData("""{ "format": "kovati-licence", "v": 1, "kid": "dev1", "sig": "AA" }""")]
    [InlineData("""not json""")]
    [InlineData("""[]""")]
    public void A_malformed_envelope_is_refused(string json) => Assert.Null(LicenceVerifier.Verify(json, _key.Ring(), out _));

    [Fact]
    public void A_signed_payload_with_the_wrong_issuer_or_version_is_refused()
    {
        var wrongIss = TestLicenceKey.Payload(Uid, T0).Replace("\"iss\":\"kovati\"", "\"iss\":\"someone\"");
        Assert.Null(LicenceVerifier.Verify(_key.Envelope(wrongIss), _key.Ring(), out var e1));
        Assert.Contains("issuer", e1);
        var wrongV = TestLicenceKey.Payload(Uid, T0).Replace("\"v\":1", "\"v\":2");
        Assert.Null(LicenceVerifier.Verify(_key.Envelope(wrongV), _key.Ring(), out _));
    }

    // ── the store ────────────────────────────────────────────────────────

    [Fact]
    public void The_store_keeps_the_envelope_byte_for_byte()
    {
        var body = Encoding.UTF8.GetBytes(_key.Envelope(Uid, T0, Bought).Replace(",", ",  ")); // spacing the API might use
        Assert.Equal(LicenceSave.Stored, Store().Offer(body, Uid, out _));
        Assert.Equal(body, File.ReadAllBytes(Store().FilePath));
        Assert.NotNull(Store().Load());
    }

    [Fact]
    public void A_licence_for_another_account_is_not_stored_and_a_stored_one_is_discarded()
    {
        Assert.Equal(LicenceSave.WrongAccount, Store().Offer(_key.Envelope(OtherUid, T0, Bought), Uid, out _));
        Assert.False(File.Exists(Store().FilePath));

        Assert.Equal(LicenceSave.Stored, Store().Offer(_key.Envelope(OtherUid, T0, Bought), OtherUid, out _));
        Assert.False(Store().DiscardIfNotFor(OtherUid));
        Assert.True(Store().DiscardIfNotFor(Uid));
        Assert.False(File.Exists(Store().FilePath));
    }

    [Fact]
    public void Only_a_newer_licence_replaces_the_stored_one()
    {
        var s = Store();
        Assert.Equal(LicenceSave.Stored, s.Offer(_key.Envelope(Uid, T0, Bought), Uid, out _));
        Assert.Equal(LicenceSave.NotNewer, s.Offer(_key.Envelope(Uid, T0.AddHours(-1)), Uid, out _));
        Assert.Equal(LicenceSave.NotNewer, s.Offer(_key.Envelope(Uid, T0), Uid, out _));
        Assert.True(s.Load()!.Payload.Products.ContainsKey("TerrainForge"));
        Assert.Equal(LicenceSave.Stored, s.Offer(_key.Envelope(Uid, T0.AddHours(1)), Uid, out _));
        Assert.Empty(s.Load()!.Payload.Products); // an account that owns nothing still gets a valid, empty licence
    }

    [Fact]
    public void A_licence_that_does_not_verify_never_replaces_a_good_one()
    {
        var s = Store();
        s.Offer(_key.Envelope(Uid, T0, Bought), Uid, out _);
        var before = File.ReadAllBytes(s.FilePath);
        Assert.Equal(LicenceSave.Invalid, s.Offer(TestLicenceKey.Generate("dev1").Envelope(Uid, T0.AddDays(1)), Uid, out _));
        Assert.Equal(before, File.ReadAllBytes(s.FilePath));
    }

    [Fact]
    public void The_replace_is_atomic_and_leaves_no_temporary_files()
    {
        var s = Store();
        s.Offer(_key.Envelope(Uid, T0, Bought), Uid, out _);
        // A plugin holding the file open: Windows will not replace it. The old file stays whole, nothing half-written.
        var old = File.ReadAllBytes(s.FilePath);
        using (var reader = new FileStream(s.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(LicenceSave.WriteFailed, s.Offer(_key.Envelope(Uid, T0.AddHours(2)), Uid, out _));
            var seen = new byte[old.Length];
            reader.ReadExactly(seen);
            Assert.Equal(old, seen);
        }
        Assert.Equal(old, File.ReadAllBytes(s.FilePath));
        // A reader that lets go within the half second does not stop the replace.
        var late = new FileStream(s.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _ = Task.Delay(150).ContinueWith(_ => late.Dispose());
        Assert.Equal(LicenceSave.Stored, s.Offer(_key.Envelope(Uid, T0.AddHours(2)), Uid, out _));
        Assert.Equal(T0.AddHours(2), s.Load()!.Payload.IssuedAt);
        Assert.Equal([LicenceStore.FileName], _dir.GetFiles().Select(f => f.Name));
    }

    [Fact]
    public void A_dev_key_licence_is_never_written_into_the_real_data_folder()
    {
        var real = Store(isDefault: true);
        Assert.Equal(LicenceSave.DevKeyRefused, real.Offer(_key.Envelope(Uid, T0, Bought), Uid, out _));
        Assert.False(File.Exists(real.FilePath));
    }

    [Fact]
    public void Delete_removes_the_licence_and_its_check_record()
    {
        var s = Store();
        s.Offer(_key.Envelope(Uid, T0, Bought), Uid, out _);
        s.SaveCheck(new LicenceCheck { Sub = Uid, LastSuccessAt = T0 });
        s.Delete();
        Assert.False(File.Exists(s.FilePath));
        Assert.False(File.Exists(s.CheckPath));
    }

    // ── the state function ───────────────────────────────────────────────

    private LicencePayload P(IDictionary<string, (string how, string since, int maxMajor)>? products = null, bool test = false, DateTimeOffset? issued = null) =>
        LicenceVerifier.Verify(_key.Envelope(TestLicenceKey.Payload(Uid, issued ?? T0, products ?? Bought, test)), _key.Ring(), out _)!.Payload;

    [Theory]
    [InlineData(0, LicenceStanding.Licensed, 30)]
    [InlineData(1, LicenceStanding.Licensed, 29)]
    [InlineData(23, LicenceStanding.Licensed, 7)]      // exactly 23 days: still the quiet state
    [InlineData(23.5, LicenceStanding.OfflineGrace, 7)]
    [InlineData(29.9, LicenceStanding.OfflineGrace, 1)]
    [InlineData(30, LicenceStanding.Expired, 0)]
    [InlineData(400, LicenceStanding.Expired, 0)]
    public void A_bought_product_across_the_30_days(double days, LicenceStanding standing, int daysLeft)
    {
        var l = LicenceRules.Evaluate(P(), Uid, "TerrainForge", T0.AddDays(days));
        Assert.Equal(standing, l.Standing);
        Assert.Equal(daysLeft, l.DaysLeft);
        Assert.Equal("bought", l.How);
        Assert.Equal(new DateOnly(2026, 11, 20), l.Since);
    }

    [Fact]
    public void A_product_not_listed_is_not_licensed_and_an_empty_licence_lists_nothing()
    {
        Assert.Equal(LicenceStanding.NotLicensed, LicenceRules.Evaluate(P(), Uid, "SkinForge", T0).Standing);
        Assert.Equal(LicenceStanding.NotLicensed, LicenceRules.Evaluate(P(new Dictionary<string, (string, string, int)>()), Uid, "TerrainForge", T0).Standing);
    }

    [Fact]
    public void Signed_out_wrong_account_and_no_licence_yet()
    {
        Assert.Equal(LicenceStanding.SignedOut, LicenceRules.Evaluate(P(), null, "TerrainForge", T0).Standing);
        Assert.Equal(LicenceStanding.NoLicenceYet, LicenceRules.Evaluate(P(), OtherUid, "TerrainForge", T0).Standing);
        Assert.Equal(LicenceStanding.NoLicenceYet, LicenceRules.Evaluate(null, Uid, "TerrainForge", T0).Standing);
        Assert.Equal(LicenceStanding.NoLicenceYet, LicenceRules.Evaluate(null, Uid, "TerrainForge", T0, LicenceFetchOutcome.Off).Standing);
        Assert.Equal(LicenceStanding.NoLicenceYet, LicenceRules.Evaluate(null, Uid, "TerrainForge", T0, LicenceFetchOutcome.Offline).Standing);
        Assert.Equal(LicenceStanding.SignedOut, LicenceRules.Evaluate(null, Uid, "TerrainForge", T0, LicenceFetchOutcome.Refused).Standing);
    }

    [Fact]
    public void A_clock_set_back_before_the_issue_time_needs_a_refresh_and_is_not_valid_forever()
    {
        var p = P();
        Assert.Equal(LicenceStanding.Licensed, LicenceRules.Evaluate(p, Uid, "TerrainForge", T0 - TimeSpan.FromMinutes(10)).Standing); // skew within tolerance
        var back = LicenceRules.Evaluate(p, Uid, "TerrainForge", T0 - TimeSpan.FromDays(3));
        Assert.Equal(LicenceStanding.Expired, back.Standing);
        Assert.True(back.ClockBehind);
        Assert.False(back.CountsAsLicensed);
        Assert.Equal(LicenceStanding.Expired, LicenceRules.Evaluate(p, Uid, "TerrainForge", T0 - TimeSpan.FromDays(3000)).Standing);
    }

    [Fact]
    public void A_far_offlineUntil_is_capped_at_30_days_after_issue()
    {
        var far = LicenceVerifier.Verify(_key.Envelope(TestLicenceKey.Payload(Uid, T0, Bought, offlineUntil: T0.AddYears(5))), _key.Ring(), out _)!.Payload;
        Assert.Equal(T0.AddDays(30), far.ValidUntil);
        Assert.Equal(LicenceStanding.Expired, LicenceRules.Evaluate(far, Uid, "TerrainForge", T0.AddDays(31)).Standing);
        var near = LicenceVerifier.Verify(_key.Envelope(TestLicenceKey.Payload(Uid, T0, Bought, offlineUntil: T0.AddDays(10))), _key.Ring(), out _)!.Payload;
        Assert.Equal(LicenceStanding.Expired, LicenceRules.Evaluate(near, Uid, "TerrainForge", T0.AddDays(11)).Standing);
    }

    [Fact]
    public void Test_licences_and_major_versions()
    {
        Assert.True(LicenceRules.Evaluate(P(test: true), Uid, "TerrainForge", T0).Test);
        Assert.True(LicenceRules.Evaluate(P(new Dictionary<string, (string, string, int)> { ["TerrainForge"] = ("test", "2026-11-20", 1) }), Uid, "TerrainForge", T0).Test);
        Assert.Equal(LicenceStanding.Licensed, LicenceRules.Evaluate(P(), Uid, "TerrainForge", T0, productMajor: 1).Standing);
        Assert.Equal(LicenceStanding.Licensed, LicenceRules.Evaluate(P(), Uid, "TerrainForge", T0, productMajor: 0).Standing);
        var two = LicenceRules.Evaluate(P(), Uid, "TerrainForge", T0, productMajor: 2);
        Assert.Equal(LicenceStanding.NotLicensed, two.Standing);
        Assert.True(two.BeyondMajor);
    }

    // ── the 20-hour rule ─────────────────────────────────────────────────

    [Fact]
    public void An_automatic_check_asks_only_once_the_last_success_is_20_hours_old()
    {
        Assert.True(LicenceRules.RefreshDue(T0, null, null, null, haveLicenceForAccount: false));
        Assert.True(LicenceRules.RefreshDue(T0, null, null, null, haveLicenceForAccount: true));
        Assert.False(LicenceRules.RefreshDue(T0.AddHours(19.9), T0, T0, LicenceFetchOutcome.Updated, true));
        Assert.True(LicenceRules.RefreshDue(T0.AddHours(20), T0, T0, LicenceFetchOutcome.Updated, true));
        Assert.True(LicenceRules.RefreshDue(T0.AddHours(-2), T0, T0, LicenceFetchOutcome.Updated, true)); // the clock went back
        Assert.True(LicenceRules.RefreshDue(T0.AddHours(1), T0, T0, LicenceFetchOutcome.Updated, false)); // the file went away
        // A server that said "not now" is left alone for an hour, file or no file.
        Assert.False(LicenceRules.RefreshDue(T0.AddMinutes(30), null, T0, LicenceFetchOutcome.RateLimited, false));
        Assert.False(LicenceRules.RefreshDue(T0.AddMinutes(30), null, T0, LicenceFetchOutcome.Off, false));
        Assert.True(LicenceRules.RefreshDue(T0.AddMinutes(61), null, T0, LicenceFetchOutcome.Off, false));
        // Offline is not the server's answer: the next check tries again.
        Assert.True(LicenceRules.RefreshDue(T0.AddMinutes(15), null, T0.AddDays(-2), LicenceFetchOutcome.Offline, false));
    }

    // ── the words ────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_says_a_tool_is_limited_while_its_free_tier_is_not_live()
    {
        var p = P();
        foreach (var standing in new[] { LicenceStanding.NotLicensed, LicenceStanding.Expired, LicenceStanding.SignedOut, LicenceStanding.NoLicenceYet })
            Assert.Equal("", LicenceWords.Line(new ProductLicence(standing) { DaysLeft = 3 }, limitedTierLive: false));
        Assert.Equal("Licensed · bought 20 Nov 2026", LicenceWords.Line(LicenceRules.Evaluate(p, Uid, "TerrainForge", T0), false));
        Assert.Equal("Licensed · bought 20 Nov 2026", LicenceWords.Line(LicenceRules.Evaluate(p, Uid, "TerrainForge", T0.AddDays(25)), false));
        Assert.Equal("Test licence", LicenceWords.Line(LicenceRules.Evaluate(P(test: true), Uid, "TerrainForge", T0), false));
    }

    [Fact]
    public void The_lines_once_a_free_tier_is_live()
    {
        var p = P();
        Assert.Equal("Offline, 5 days left", LicenceWords.Line(LicenceRules.Evaluate(p, Uid, "TerrainForge", T0.AddDays(25)), true));
        Assert.Equal("Offline, 1 day left", LicenceWords.Line(LicenceRules.Evaluate(p, Uid, "TerrainForge", T0.AddDays(29.5)), true));
        Assert.Equal("Running as Lite until you are online", LicenceWords.Line(LicenceRules.Evaluate(p, Uid, "TerrainForge", T0.AddDays(31)), true));
        Assert.Equal("Running as a trial until you are online", LicenceWords.Line(LicenceRules.Evaluate(p, Uid, "TerrainForge", T0.AddDays(31)), true, trial: true));
        Assert.Equal("Not licensed: this runs as Lite", LicenceWords.Line(LicenceRules.Evaluate(p, Uid, "SkinForge", T0), true));
        Assert.Equal("Sign in to get Full back", LicenceWords.Line(LicenceRules.Evaluate(p, null, "TerrainForge", T0), true));
        Assert.StartsWith("This computer's clock is behind", LicenceWords.Line(LicenceRules.Evaluate(p, Uid, "TerrainForge", T0.AddDays(-2)), true));
        Assert.Equal("", LicenceWords.Line(LicenceRules.Evaluate(null, Uid, "TerrainForge", T0, LicenceFetchOutcome.Off), true));
        Assert.Equal("Licensed · from Fab", LicenceWords.Line(new ProductLicence(LicenceStanding.Licensed) { How = "fab" }, true));
        Assert.Equal("Licensed · through Pro", LicenceWords.Line(new ProductLicence(LicenceStanding.Licensed) { How = "pro" }, true));
        Assert.Equal("Licensed · granted 1 Dec 2026", LicenceWords.Line(new ProductLicence(LicenceStanding.Licensed) { How = "granted", Since = new DateOnly(2026, 12, 1) }, true));
    }

    // ── the service over HTTP ────────────────────────────────────────────

    private sealed class Rig
    {
        public required LicenceService Service { get; init; }
        public required StubHandler Handler { get; init; }
        public required LicenceStore Store { get; init; }
        public DateTimeOffset Now { get; set; }
        public StoredAccount? Account { get; set; } = new(Uid, "a@kovati.dev", "refresh");
        public int Changes { get; set; }
    }

    private Rig Service(Func<HttpRequestMessage, HttpResponseMessage> answer, bool tokenFails = false)
    {
        var handler = new StubHandler(answer);
        var store = Store();
        Rig? rig = null;
        var service = new LicenceService(new HttpClient(handler), () => rig!.Account,
            (a, ct) => tokenFails ? throw new EntitlementException("revoked", "unauthenticated") : Task.FromResult("id-token-" + a.Uid),
            store, () => rig!.Now, () => "http://api.test/api/");
        rig = new Rig { Service = service, Handler = handler, Store = store, Now = T0 };
        service.Changed += () => rig.Changes++;
        return rig;
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string? body = null) =>
        new(code) { Content = new StringContent(body ?? "", Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_200_is_verified_stored_and_sent_with_the_id_token()
    {
        var rig = Service(_ => StubHandler.Json(_key.Envelope(Uid, T0, Bought)));
        Assert.Equal(LicenceFetchOutcome.Updated, await rig.Service.RefreshAsync(force: true));
        var req = Assert.Single(rig.Handler.Requests);
        Assert.Equal("http://api.test/api/licence", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("id-token-" + Uid, req.Headers.Authorization.Parameter);
        Assert.Equal(1, rig.Changes);
        var snap = rig.Service.Snapshot();
        Assert.Equal(LicenceStanding.Licensed, snap.For("TerrainForge").Standing);
        Assert.Equal(T0, snap.Check.LastSuccessAt);
        Assert.Equal(LicenceFetchOutcome.Updated, snap.Check.LastOutcome);
    }

    [Fact]
    public async Task The_same_licence_again_is_unchanged_and_counts_as_a_success()
    {
        var env = _key.Envelope(Uid, T0, Bought);
        var rig = Service(_ => StubHandler.Json(env));
        await rig.Service.RefreshAsync(true);
        rig.Now = T0.AddHours(21);
        Assert.Equal(LicenceFetchOutcome.Unchanged, await rig.Service.RefreshAsync(true));
        Assert.Equal(T0.AddHours(21), rig.Service.Snapshot().Check.LastSuccessAt);
        Assert.Equal(1, rig.Changes);
    }

    [Fact]
    public async Task An_automatic_refresh_within_20_hours_does_not_call_the_server()
    {
        var rig = Service(_ => StubHandler.Json(_key.Envelope(Uid, T0, Bought)));
        Assert.Equal(LicenceFetchOutcome.Updated, await rig.Service.RefreshAsync(force: false)); // start, nothing stored
        rig.Now = T0.AddHours(5);
        Assert.Equal(LicenceFetchOutcome.NotDue, await rig.Service.RefreshAsync(force: false)); // the watcher, 5 hours later
        rig.Now = T0.AddHours(19);
        Assert.Equal(LicenceFetchOutcome.NotDue, await rig.Service.RefreshAsync(force: false));
        Assert.Single(rig.Handler.Requests);
        Assert.Equal(LicenceFetchOutcome.Unchanged, await rig.Service.RefreshAsync(force: true)); // Check now always asks
        Assert.Equal(2, rig.Handler.Requests.Count);
        rig.Now = T0.AddHours(40);
        Assert.NotEqual(LicenceFetchOutcome.NotDue, await rig.Service.RefreshAsync(force: false));
        Assert.Equal(3, rig.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_401_or_403_removes_the_licence(HttpStatusCode code)
    {
        var answer = StubHandler.Json(_key.Envelope(Uid, T0, Bought));
        var rig = Service(_ => answer);
        await rig.Service.RefreshAsync(true);
        answer = Status(code, """{ "error": "unauthenticated" }""");
        Assert.Equal(LicenceFetchOutcome.Refused, await rig.Service.RefreshAsync(true));
        Assert.False(File.Exists(rig.Store.FilePath));
        Assert.Equal(2, rig.Changes);
        Assert.Equal(LicenceStanding.SignedOut, rig.Service.Snapshot().For("TerrainForge").Standing);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, """{ "error": "slow down" }""", LicenceFetchOutcome.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{ "error": "Licences are not configured.", "code": "licence-off" }""", LicenceFetchOutcome.Off)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{ "error": "licence-off" }""", LicenceFetchOutcome.Off)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "<html>down</html>", LicenceFetchOutcome.ServerError)]
    [InlineData(HttpStatusCode.InternalServerError, "", LicenceFetchOutcome.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, "", LicenceFetchOutcome.ServerError)]
    [InlineData(HttpStatusCode.NotFound, "", LicenceFetchOutcome.Off)]
    public async Task Server_trouble_keeps_the_stored_licence_silently(HttpStatusCode code, string body, LicenceFetchOutcome expected)
    {
        var answer = StubHandler.Json(_key.Envelope(Uid, T0, Bought));
        var rig = Service(_ => answer);
        await rig.Service.RefreshAsync(true);
        var before = File.ReadAllBytes(rig.Store.FilePath);
        answer = Status(code, body);
        rig.Now = T0.AddDays(2);
        Assert.Equal(expected, await rig.Service.RefreshAsync(true));
        Assert.Equal(before, File.ReadAllBytes(rig.Store.FilePath));
        Assert.Equal(LicenceStanding.Licensed, rig.Service.Snapshot().For("TerrainForge").Standing);
        // And the automatic check leaves the server alone for the next hour.
        rig.Now = T0.AddDays(2).AddMinutes(20);
        if (expected != LicenceFetchOutcome.Unchanged)
            Assert.Equal(LicenceFetchOutcome.NotDue, await rig.Service.RefreshAsync(false));
    }

    [Fact]
    public async Task Licence_off_with_nothing_stored_is_no_licence_yet()
    {
        var rig = Service(_ => Status(HttpStatusCode.ServiceUnavailable, """{ "code": "licence-off" }"""));
        Assert.Equal(LicenceFetchOutcome.Off, await rig.Service.RefreshAsync(false));
        var snap = rig.Service.Snapshot();
        Assert.True(snap.ServerOff);
        Assert.Equal(LicenceStanding.NoLicenceYet, snap.For("TerrainForge").Standing);
    }

    [Fact]
    public async Task Offline_keeps_the_stored_licence()
    {
        var online = true;
        var rig = Service(_ => online ? StubHandler.Json(_key.Envelope(Uid, T0, Bought)) : throw new HttpRequestException("No such host is known."));
        await rig.Service.RefreshAsync(true);
        online = false;
        rig.Now = T0.AddDays(24);
        Assert.Equal(LicenceFetchOutcome.Offline, await rig.Service.RefreshAsync(true));
        Assert.True(File.Exists(rig.Store.FilePath));
        var l = rig.Service.Snapshot().For("TerrainForge");
        Assert.Equal(LicenceStanding.OfflineGrace, l.Standing);
        Assert.Equal(6, l.DaysLeft);
    }

    [Fact]
    public async Task A_timeout_counts_as_offline()
    {
        var rig = Service(_ => throw new TaskCanceledException("timeout"));
        Assert.Equal(LicenceFetchOutcome.Offline, await rig.Service.RefreshAsync(true));
    }

    [Fact]
    public async Task A_licence_for_another_account_from_the_server_is_discarded()
    {
        var rig = Service(_ => StubHandler.Json(_key.Envelope(OtherUid, T0, Bought)));
        Assert.Equal(LicenceFetchOutcome.Invalid, await rig.Service.RefreshAsync(true));
        Assert.False(File.Exists(rig.Store.FilePath));
    }

    [Fact]
    public async Task A_bad_signature_from_the_server_keeps_the_good_stored_one()
    {
        var answer = StubHandler.Json(_key.Envelope(Uid, T0, Bought));
        var rig = Service(_ => answer);
        await rig.Service.RefreshAsync(true);
        answer = StubHandler.Json(TestLicenceKey.Generate("dev1").Envelope(Uid, T0.AddDays(1)));
        Assert.Equal(LicenceFetchOutcome.Invalid, await rig.Service.RefreshAsync(true));
        Assert.True(rig.Store.Load()!.Payload.Products.ContainsKey("TerrainForge"));
    }

    [Fact]
    public async Task Switching_accounts_drops_the_other_accounts_licence_before_asking()
    {
        var rig = Service(r => StubHandler.Json(_key.Envelope(r.Headers.Authorization!.Parameter!.Replace("id-token-", ""), T0, Bought)));
        await rig.Service.RefreshAsync(true);
        rig.Account = new StoredAccount(OtherUid, "b@kovati.dev", "refresh-b");
        Assert.Equal(LicenceStanding.NoLicenceYet, rig.Service.Snapshot().For("TerrainForge").Standing); // never shown as b's
        Assert.Equal(LicenceFetchOutcome.Updated, await rig.Service.RefreshAsync(false)); // b has nothing stored: due at once
        Assert.Equal(OtherUid, rig.Store.Load()!.Payload.Sub);
    }

    [Fact]
    public async Task Signed_out_removes_any_licence_and_asks_nothing()
    {
        var rig = Service(_ => StubHandler.Json(_key.Envelope(Uid, T0, Bought)));
        await rig.Service.RefreshAsync(true);
        rig.Account = null;
        Assert.Equal(LicenceFetchOutcome.NotSignedIn, await rig.Service.RefreshAsync(true));
        Assert.False(File.Exists(rig.Store.FilePath));
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task A_sign_in_that_no_longer_renews_keeps_the_file_to_run_out_by_itself()
    {
        var store = Store();
        store.Offer(_key.Envelope(Uid, T0, Bought), Uid, out _);
        var rig = Service(_ => StubHandler.Json(""), tokenFails: true);
        Assert.Equal(LicenceFetchOutcome.NotSignedIn, await rig.Service.RefreshAsync(true));
        Assert.True(File.Exists(store.FilePath));
        Assert.Empty(rig.Handler.Requests);
    }
}
