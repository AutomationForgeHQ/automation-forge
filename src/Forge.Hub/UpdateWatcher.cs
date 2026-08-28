using System.Text.Json;
using Forge.Core;
using Forge.Core.Engines;
using Forge.Core.Installs;
using Forge.Core.Manifest;
using Forge.Core.Releases;

namespace Forge.Hub;

public sealed record PendingUpdate(string Plugin, string From, string To, string Where);

public sealed record WatchResult(IReadOnlyList<PendingUpdate> Plugins, HubRelease? Hub, DateTimeOffset CheckedAt)
{
    public int Count => Plugins.Select(p => p.Plugin).Distinct().Count() + (Hub is null ? 0 : 1);
}

/// <summary>
/// Looks, every few hours, at what the receipts say is installed — on every
/// engine and every project the hub installed into — against what the releases
/// say is newest on the chosen channel, and at the hub's own releases. Installs
/// nothing. Announces a given version once; a check that finds only what has
/// already been announced stays quiet.
/// </summary>
public sealed class UpdateWatcher : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    private readonly Settings _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly ManifestClient _manifest;
    private readonly ReleaseDiscovery _releases;
    private readonly HubReleases _hub;
    private readonly string _notifiedFile = Path.Combine(Paths.DataDir, "notified.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;

    public WatchResult? Last { get; private set; }

    /// <summary>Every completed check.</summary>
    public event Action<WatchResult>? Checked;

    /// <summary>A check that found versions not announced before. The list is their keys (plugin@version, hub@version).</summary>
    public event Action<WatchResult, IReadOnlyList<string>>? NewFindings;

    public UpdateWatcher(Settings settings)
    {
        _settings = settings;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent("hub-watch"));
        _manifest = new ManifestClient(_http);
        _releases = new ReleaseDiscovery(_http);
        _hub = new HubReleases(_http);
    }

    public void Start(TimeSpan firstDelay)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = LoopAsync(firstDelay, _cts.Token);
    }

    private async Task LoopAsync(TimeSpan firstDelay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(firstDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                await CheckAsync(ct);
                await Task.Delay(Interval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One check. Null when the network and the cache both had nothing to say.</summary>
    public async Task<WatchResult?> CheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (manifest, _) = await _manifest.GetAsync(ct: ct);
            await _releases.MergeIntoAsync(manifest, ct: ct);
            var state = new InstallState();
            var pending = new List<PendingUpdate>();

            foreach (var engine in EngineLocator.Find())
            {
                var target = InstallTarget.Engine(engine);
                foreach (var i in state.On(target))
                    if (Newer(manifest, i, engine.Version) is { } to) pending.Add(new PendingUpdate(i.Plugin, i.Version, to, target.Label));
            }
            foreach (var i in state.Items.Where(i => i.TargetKind == "project"))
            {
                var projectDir = Path.GetDirectoryName(Path.GetDirectoryName(i.TargetRoot));
                if (Newer(manifest, i, i.Engine) is { } to) pending.Add(new PendingUpdate(i.Plugin, i.Version, to, Path.GetFileName(projectDir) ?? "project"));
            }

            HubRelease? hub = null;
            if (_settings.CheckForUpdates) hub = await _hub.NewerThanAsync(AppInfo.SemVer, _settings.Channel, ct: ct);

            var result = new WatchResult(pending, hub, DateTimeOffset.Now);
            Last = result;
            Checked?.Invoke(result);

            var keys = pending.Select(u => $"{u.Plugin}@{u.To}").Distinct().ToList();
            if (hub is not null) keys.Add($"hub@{hub.Version}");
            var seen = LoadNotified();
            var fresh = keys.Where(k => !seen.Contains(k)).ToList();
            if (fresh.Count > 0)
            {
                seen.UnionWith(fresh);
                SaveNotified(seen);
                NewFindings?.Invoke(result, fresh);
            }
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or InvalidDataException or JsonException or TaskCanceledException)
        {
            return null;
        }
        finally { _gate.Release(); }
    }

    private string? Newer(Manifest manifest, InstalledPlugin installed, string engineVersion)
    {
        var p = manifest.Plugin(installed.Plugin);
        var latest = p?.Latest(engineVersion, _settings.Channel);
        return latest is not null && latest.Version != installed.Version ? latest.Version : null;
    }

    private HashSet<string> LoadNotified()
    {
        try
        {
            if (File.Exists(_notifiedFile))
                return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_notifiedFile)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return [];
    }

    private void SaveNotified(HashSet<string> seen)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(_notifiedFile, JsonSerializer.Serialize(seen));
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _http.Dispose();
    }
}
