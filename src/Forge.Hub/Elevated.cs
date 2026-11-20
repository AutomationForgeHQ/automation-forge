using Forge.Core;
using Forge.Core.Engines;
using Forge.Core.Entitlements;
using Forge.Core.Installs;
using Forge.Core.Manifest;
using Forge.Core.Releases;

namespace Forge.Hub;

/// <summary>
/// The headless half of the hub. `Forge.Hub --elevated install|uninstall|migrate
/// &lt;ids…&gt; --engine &lt;path&gt;` runs under UAC, writes what it did to a log
/// file the windowed hub reads back, and exits 0 on success.
/// </summary>
public static class Elevated
{
    public const string Flag = "--elevated";
    public static string LogPath => Path.Combine(Paths.DataDir, HubIdentity.ElevatedLog);

    public static int Run(string[] args)
    {
        var lines = new List<string>();
        // Each line goes to the log as it happens: the window follows the file and ticks components off (4.5).
        Directory.CreateDirectory(Paths.DataDir);
        try { File.WriteAllText(LogPath, ""); } catch (IOException) { }
        void Say(string s)
        {
            lines.Add(s);
            Console.WriteLine(s);
            try { File.AppendAllLines(LogPath, [s]); } catch (IOException) { }
        }
        try
        {
            var verb = args.Length > 0 ? args[0] : "";
            var ids = new List<string>();
            string? engine = null, project = null; var channel = Settings.Stable;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--engine" && i + 1 < args.Length) engine = args[++i];
                else if (args[i] == "--project" && i + 1 < args.Length) project = args[++i];
                else if (args[i] == "--channel" && i + 1 < args.Length) channel = args[++i];
                else ids.Add(args[i]);
            }
            var e = EngineLocator.Resolve(engine) ?? throw new InvalidOperationException($"No engine at {engine}.");
            // A project the window could not write to (a folder that needs rights): builds for its engine, into the project.
            var target = project is null ? InstallTarget.Engine(e) : InstallTarget.Project(project);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent("hub-elevated"));
            var state = new InstallState();
            // The signed-in account of this Windows user: an anonymous provider refuses every download (since
            // 2026-09-06 nothing installs without an account), so an engine in Program Files could not install at all.
            var installer = new Installer(http, state, new FirebaseEntitlements(http), Say);

            if (verb == "uninstall")
            {
                foreach (var id in ids) if (!installer.Uninstall(target, id)) Say($"not installed: {id}");
                return Finish(0);
            }

            if (verb == "migrate")
            {
                var left = installer.MoveLegacyEngineInstalls(target);
                return Finish(left == 0 ? 0 : 1);
            }

            var (manifest, _) = new ManifestClient(http).GetAsync().GetAwaiter().GetResult();
            new ReleaseDiscovery(http).MergeIntoAsync(manifest).GetAwaiter().GetResult();
            var failures = 0;
            foreach (var id in manifest.WithHubPlugin(ids, target.Kind))
            {
                var p = manifest.Plugin(id);
                if (p is null) { Say($"unknown plugin: {id}"); failures++; continue; }
                var v = p.Latest(e.Version, channel);
                if (v is null) { Say($"skipped: {id} has no {channel} release for UE {e.Version}"); continue; }
                Say($"start: {id} {v.Version}");
                try
                {
                    var r = installer.InstallAsync(new InstallRequest(p, v, target)).GetAwaiter().GetResult();
                    if (r.Outcome == "already-current") Say($"current: {id} {v.Version}");
                }
                catch (Exception ex) when (ex is EntitlementException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or IOException)
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
            return code;
        }
    }
}
