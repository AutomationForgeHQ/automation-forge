using System.Net;
using System.Text.Json.Nodes;
using Forge.Core.Installs;
using Forge.Core.Manifest;
using Forge.Core.Releases;
using Xunit;

namespace Forge.Core.Tests;

/// <summary>
/// Kovati Hub v2 as a beta beside the Automation Forge Hub 0.4.x: its own release line, and the shared data folder
/// written without losing the other Hub's entries.
/// </summary>
public class BetaSideBySideTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("forge-beta-");

    public void Dispose() => _dir.Delete(true);

    // The release list as GitHub returns it, newest first: both lines in one repository.
    private const string Releases = """
    [
      { "tag_name": "kovati-hub-v0.6.0", "draft": true, "prerelease": false,
        "assets": [ { "name": "KovatiHub-Setup.exe", "browser_download_url": "https://x/draft.exe", "size": 1 } ] },
      { "tag_name": "kovati-hub-v0.5.0-beta.3", "draft": false, "prerelease": true,
        "assets": [ { "name": "AutomationForge-Setup.exe", "browser_download_url": "https://x/wrong-asset.exe", "size": 1 } ] },
      { "tag_name": "kovati-hub-v0.5.0-beta.2", "draft": false, "prerelease": true, "html_url": "https://x/b2",
        "assets": [ { "name": "KovatiHub-Setup.exe", "browser_download_url": "https://x/b2/KovatiHub-Setup.exe", "size": 42,
                      "digest": "sha256:abc123" } ] },
      { "tag_name": "kovati-hub-vnext", "draft": false, "prerelease": true,
        "assets": [ { "name": "KovatiHub-Setup.exe", "browser_download_url": "https://x/next.exe", "size": 1 } ] },
      { "tag_name": "kovati-hub-v0.5.0-beta.1", "draft": false, "prerelease": true,
        "assets": [ { "name": "KovatiHub-Setup.exe", "browser_download_url": "https://x/b1/KovatiHub-Setup.exe", "size": 41 } ] },
      { "tag_name": "v0.4.5-nightly.20261120", "draft": false, "prerelease": true,
        "assets": [ { "name": "AutomationForge-Setup.exe", "browser_download_url": "https://x/n/AutomationForge-Setup.exe", "size": 9 },
                    { "name": "KovatiHub-Setup.exe", "browser_download_url": "https://x/n/KovatiHub-Setup.exe", "size": 9 } ] },
      { "tag_name": "v0.4.4", "draft": false, "prerelease": false,
        "assets": [ { "name": "AutomationForge-Setup.exe", "browser_download_url": "https://x/044/AutomationForge-Setup.exe", "size": 8 },
                    { "name": "KovatiHub-Setup.exe", "browser_download_url": "https://x/044/KovatiHub-Setup.exe", "size": 8 } ] }
    ]
    """;

    [Fact]
    public void The_beta_reads_only_kovati_hub_tags_that_carry_its_installer()
    {
        var list = HubReleases.Parse(Releases);
        Assert.Equal(["kovati-hub-v0.5.0-beta.2", "kovati-hub-v0.5.0-beta.1"], list.Select(r => r.Tag));
        var b2 = list[0];
        Assert.Equal(SemVer.Parse("0.5.0-beta.2"), b2.Version);
        Assert.Equal("https://x/b2/KovatiHub-Setup.exe", b2.InstallerUrl);
        Assert.Equal("abc123", b2.InstallerSha256);
        Assert.Equal(42, b2.InstallerSize);
    }

    [Fact]
    public void A_beta_on_the_stable_channel_follows_its_own_prereleases_and_nothing_else()
    {
        var list = HubReleases.Parse(Releases);
        var next = HubReleases.Newest(list, SemVer.Parse("0.5.0-beta.1"), Settings.Stable);
        Assert.Equal("kovati-hub-v0.5.0-beta.2", next?.Tag);
        Assert.Null(HubReleases.Newest(list, SemVer.Parse("0.5.0-beta.2"), Settings.Stable));
        // A stable build (not a pre-release) on the stable channel is not offered the betas.
        Assert.Null(HubReleases.Newest(list, SemVer.Parse("0.4.9"), Settings.Stable));
        Assert.Equal("kovati-hub-v0.5.0-beta.2", HubReleases.Newest(list, SemVer.Parse("0.4.9"), Settings.Nightly)?.Tag);
    }

    /// <summary>
    /// The 0.4.4 Hub's reading of the same list (its HubReleases.ListAsync: every tag SemVer.TryParse accepts, the
    /// asset AutomationForge-Setup.exe; SemVer.cs is the same file on v0.4.4, main and hub-v2): no kovati-hub tag
    /// gets through, so an old Hub is never offered the beta.
    /// </summary>
    [Fact]
    public void A_0_4_x_Hub_reading_the_same_list_never_sees_a_beta_release()
    {
        Assert.False(SemVer.TryParse("kovati-hub-v0.5.0-beta.1", out _));
        Assert.False(SemVer.TryParse("kovati-hub-v0.5.0", out _));
        var old = HubReleases.Parse(Releases, tagPrefix: "", asset: "AutomationForge-Setup.exe");
        Assert.Equal(["v0.4.5-nightly.20261120", "v0.4.4"], old.Select(r => r.Tag));
        // And on its stable channel, a 0.4.4 Hub skips pre-releases altogether.
        Assert.Null(HubReleases.Newest(old, SemVer.Parse("0.4.4"), Settings.Stable));
        Assert.Equal("v0.4.5-nightly.20261120", HubReleases.Newest(old, SemVer.Parse("0.4.4"), Settings.Nightly)?.Tag);
    }

    private InstalledPlugin Receipt(string plugin)
    {
        var path = Directory.CreateDirectory(Path.Combine(_dir.FullName, "engine", plugin)).FullName;
        return new InstalledPlugin
        {
            Plugin = plugin, Version = "1.0.0", Engine = "5.8", TargetKind = "engine",
            TargetRoot = Path.Combine(_dir.FullName, "engine"), Path = path, InstalledAt = "2026-11-19T00:00:00Z",
        };
    }

    [Fact]
    public void Two_Hubs_recording_from_stale_copies_keep_each_others_receipts()
    {
        var file = Path.Combine(_dir.FullName, "installed.json");
        var beta = new InstallState(file);
        var other = new InstallState(file); // read before either wrote
        beta.Record(Receipt("MotionForge"));
        other.Record(Receipt("FaceForge"));
        beta.Record(Receipt("SpeechForge"));
        var now = new InstallState(file);
        Assert.Equal(["FaceForge", "MotionForge", "SpeechForge"], now.Items.Select(i => i.Plugin).Order());
        Assert.Equal(3, beta.Items.Count);
    }

    [Fact]
    public void Forgetting_from_a_stale_copy_removes_only_that_receipt()
    {
        var file = Path.Combine(_dir.FullName, "installed.json");
        var beta = new InstallState(file);
        beta.Record(Receipt("MotionForge"));
        var stale = new InstallState(file);
        beta.Record(Receipt("FaceForge"));
        var target = new InstallTarget("engine", Path.Combine(_dir.FullName, "engine"), "UE 5.8");
        stale.Forget(target, "MotionForge");
        Assert.Equal(["FaceForge"], new InstallState(file).Items.Select(i => i.Plugin));
    }

    [Fact]
    public void Settings_write_only_what_this_Hub_changed_and_keep_unknown_fields()
    {
        var file = Path.Combine(_dir.FullName, "settings.json");
        File.WriteAllText(file, """{ "channel": "stable", "checkForUpdates": true, "runInBackground": true, "notifyOnUpdates": true, "extraPluginRoot": "", "futureField": 7 }""");
        var beta = Settings.Load(file);
        var old = Settings.Load(file);
        old.Channel = Settings.Nightly;
        old.Save();
        beta.NotifyOnUpdates = false;
        beta.Save();
        var now = Settings.Load(file);
        Assert.Equal(Settings.Nightly, now.Channel);
        Assert.False(now.NotifyOnUpdates);
        Assert.Equal(7, (int)JsonNode.Parse(File.ReadAllText(file))!["futureField"]!);
    }

    [Fact]
    public async Task The_manifest_is_asked_for_with_its_ETag_and_a_304_serves_the_cache()
    {
        const string body = """{ "schemaVersion": 1, "plugins": [], "sets": [] }""";
        var handler = new StubHandler(req => req.Headers.IfNoneMatch.Count > 0
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : StubHandler.Json(body, "\"m1\""));
        var client = new ManifestClient(new HttpClient(handler), "https://example.invalid/manifest.json", _dir.FullName);
        var (_, cached1) = await client.GetAsync();
        Assert.False(cached1);
        Assert.True(client.LastChanged);
        var (m, cached2) = await client.GetAsync();
        Assert.False(cached2);
        Assert.False(client.LastChanged);
        Assert.NotNull(m);
        Assert.Equal("\"m1\"", handler.Requests[1].Headers.IfNoneMatch.Single().ToString());

        // Somebody else (the CLI, a 0.4.x Hub) rewrote the cache: its ETag no longer belongs to it and is not sent.
        File.WriteAllText(Path.Combine(_dir.FullName, "manifest.json"), body.Replace("[]", "[ ]"));
        await client.GetAsync();
        Assert.Empty(handler.Requests[2].Headers.IfNoneMatch);
    }
}
