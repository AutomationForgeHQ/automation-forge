using Forge.Core;
using Forge.Core.Engines;
using Forge.Core.Entitlements;
using Forge.Core.Installs;
using Forge.Core.Manifest;
using Forge.Core.Releases;

namespace Forge.Hub;

/// <summary>
/// The headless half of the hub. `Forge.Hub --elevated install|uninstall
/// &lt;ids…&gt; --engine &lt;path&gt;` runs under UAC, writes what it did to a log
/// file the windowed hub reads back, and exits 0 on success.
/// </summary>
public static class Elevated
{
    public const string Flag = "--elevated";
    public static string LogPath => Path.Combine(Paths.DataDir, "elevated.log");

    public static int Run(string[] args)
    {
        var lines = new List<string>();
        void Say(string s) { lines.Add(s); Console.WriteLine(s); }
        try
        {
            var verb = args.Length > 0 ? args[0] : "";
            var ids = new List<string>();
            string? engine = null; var channel = Settings.Stable;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--engine" && i + 1 < args.Length) engine = args[++i];
                else if (args[i] == "--channel" && i + 1 < args.Length) channel = args[++i];
                else ids.Add(args[i]);
            }
            var e = EngineLocator.Resolve(engine) ?? throw new InvalidOperationException($"No engine at {engine}.");
            var target = InstallTarget.Engine(e);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent("hub-elevated"));
            var state = new InstallState();
            var installer = new Installer(http, state, new AnonymousEntitlements(), Say);

            if (verb == "uninstall")
            {
                foreach (var id in ids) if (!installer.Uninstall(target, id)) Say($"not installed: {id}");
                return Finish(0);
            }

            var (manifest, _) = new ManifestClient(http).GetAsync().GetAwaiter().GetResult();
            new ReleaseDiscovery(http).MergeIntoAsync(manifest).GetAwaiter().GetResult();
            var failures = 0;
            foreach (var id in ids)
            {
                var p = manifest.Plugin(id);
                if (p is null) { Say($"unknown plugin: {id}"); failures++; continue; }
                var v = p.Latest(e.Version, channel);
                if (v is null) { Say($"skipped: {id} has no {channel} release for UE {e.Version}"); continue; }
                try
                {
                    var r = installer.InstallAsync(new InstallRequest(p, v, target)).GetAwaiter().GetResult();
                    if (r.Outcome == "already-current") Say($"current: {id} {v.Version}");
                }
                catch (Exception ex) when (ex is EntitlementException or InvalidDataException or HttpRequestException or UnauthorizedAccessException)
                {
                    Say($"failed: {id} — {ex.Message}"); failures++;
                }
            }
            return Finish(failures == 0 ? 0 : 1);
        }
        catch (Exception ex)
        {
            Say($"failed: {ex.Message}");
            return Finish(2);
        }

        int Finish(int code)
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllLines(LogPath, lines);
            return code;
        }
    }
}
