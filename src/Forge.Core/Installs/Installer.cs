using System.IO.Compression;
using Forge.Core.Entitlements;
using Forge.Core.Manifest;

namespace Forge.Core.Installs;

public sealed record InstallRequest(PluginInfo Plugin, VersionInfo Version, InstallTarget Target);

public sealed class InstallResult
{
    public required string Plugin { get; init; }
    public required string Version { get; init; }
    public required string Path { get; init; }
    public required string Outcome { get; init; } // installed · updated · already-current · skipped
}

/// <summary>
/// Download, verify, place. Every step is checked before the next: the account
/// must hold the plugin (a free one is added on the way), the archive's sha256
/// must match the manifest, the archive must contain exactly one plugin folder
/// with a descriptor, and the target must be writable — the caller is told to
/// elevate rather than failing halfway through a Program Files write.
/// </summary>
public sealed class Installer
{
    private readonly HttpClient _http;
    private readonly InstallState _state;
    private readonly IEntitlementProvider _entitlements;
    private readonly Action<string> _log;

    public Installer(HttpClient http, InstallState state, IEntitlementProvider entitlements, Action<string>? log = null)
    {
        _http = http; _state = state; _entitlements = entitlements; _log = log ?? (_ => { });
    }

    public static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".forge-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>Install one plugin (dependencies are the caller's concern — see Manifest.Closure).</summary>
    public async Task<InstallResult> InstallAsync(InstallRequest req, bool force = false, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (plugin, version, target) = (req.Plugin, req.Version, req.Target);
        var dest = target.PluginDir(plugin.Id);

        // An older hub's installs move to the current root first, so a current one is not downloaded again.
        if (target.LegacyRoot is not null && Directory.Exists(target.LegacyRoot) && IsWritable(target.Root))
            MoveLegacyEngineInstalls(target);

        var existing = _state.Find(target, plugin.Id);
        if (!force && existing is not null && existing.Version == version.Version && Directory.Exists(dest))
            return new InstallResult { Plugin = plugin.Id, Version = version.Version, Path = dest, Outcome = "already-current" };

        if (!IsWritable(target.Root))
            throw new UnauthorizedAccessException($"{target.Root} is not writable. Run elevated to install into this engine, or install into a project instead.");

        // A copy that could not move would sit beside the new one, and Unreal refuses two plugins of one name.
        if (target.LegacyPluginDir(plugin.Id) is { } stuck && Directory.Exists(stuck))
            throw new IOException($"{plugin.Id} is still in {target.LegacyRoot} and could not be moved — Unreal Editor has it loaded. Close the editor and try again.");

        // The manifest's URL is where the backend finds the package, never where a
        // client fetches it: every download is issued by the account service, for
        // an account that holds the plugin. Signed out, this throws before any bytes.
        if (!_entitlements.IsSignedIn) throw new EntitlementException(AnonymousEntitlements.SignInMessage, "unauthenticated");
        var ownedBefore = await _entitlements.OwnsAsync(plugin, ct);
        var download = await _entitlements.ResolveDownloadAsync(plugin, version, ct);
        if (!ownedBefore) _log($"added to your account: {plugin.Id}");

        var archive = await DownloadAsync(download.Url, plugin.Id, version, progress, ct);
        VerifyChecksum(archive, version.Sha256 ?? download.Sha256);

        var staging = Path.Combine(Paths.DownloadDir, $"extract-{plugin.Id}-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(archive, staging);
            var folder = Directory.GetDirectories(staging).SingleOrDefault()
                         ?? throw new InvalidDataException("The package does not contain exactly one plugin folder.");
            var descriptor = Path.Combine(folder, $"{plugin.Id}.uplugin");
            if (!File.Exists(descriptor))
                throw new InvalidDataException($"The package has no {plugin.Id}.uplugin at its root.");

            var outcome = Directory.Exists(dest) ? "updated" : "installed";
            Place(folder, descriptor, dest, plugin.Id);

            _state.Record(new InstalledPlugin
            {
                Plugin = plugin.Id, Version = version.Version, Engine = version.Engine, Channel = version.Channel,
                Sha256 = version.Sha256, TargetKind = target.Kind, TargetRoot = target.Root, Path = dest,
                InstalledAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            });
            _log($"{outcome}: {plugin.Id} {version.Version} → {dest}");
            return new InstallResult { Plugin = plugin.Id, Version = version.Version, Path = dest, Outcome = outcome };
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// Put an unpacked plugin folder at dest. The package is unpacked under %LOCALAPPDATA%, and a folder only
    /// renames within one drive, so for a project or engine on another drive it is copied over instead. Either
    /// way it is built beside dest without its descriptor — Unreal does not see a folder with no .uplugin, so a
    /// copy cut short is never a second plugin of the same name — and the descriptor goes in last. The installed
    /// version is set aside rather than deleted until the new one is in place, so an update that fails leaves it.
    /// </summary>
    private static void Place(string folder, string descriptor, string dest, string plugin)
    {
        var root = Path.GetDirectoryName(dest)!;
        Directory.CreateDirectory(root);
        foreach (var stale in Directory.GetDirectories(root, $".{plugin}.forge-*"))
            TryDelete(stale, plugin);

        var name = Path.GetFileName(descriptor);
        var heldDescriptor = Path.Combine(Path.GetDirectoryName(folder)!, name);
        File.Move(descriptor, heldDescriptor);

        var ready = Path.Combine(root, $".{plugin}.forge-partial-{Guid.NewGuid():N}");
        string? old = null;
        try
        {
            // Same drive is one rename. Another drive fails here, also when the drive letter matches but a
            // junction points elsewhere, which a comparison of path roots would miss.
            try { Directory.Move(folder, ready); }
            catch (IOException) { CopyTree(folder, ready); }

            // The installed version steps aside in one rename, which fails whole when the editor holds any of
            // its files — deleting it outright would get partway and leave a plugin with pieces missing.
            if (Directory.Exists(dest))
            {
                old = Path.Combine(root, $".{plugin}.forge-old-{Guid.NewGuid():N}");
                try { Directory.Move(dest, old); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    old = null;
                    throw new IOException($"{plugin} is in use — Unreal Editor has it loaded. Close the editor and try again. ({ex.Message})");
                }
            }
            Directory.Move(ready, dest);
        }
        catch
        {
            if (old is not null && !Directory.Exists(dest)) Directory.Move(old, dest);
            if (Directory.Exists(ready)) TryDelete(ready, plugin);
            throw;
        }
        File.Copy(heldDescriptor, Path.Combine(dest, name));
        if (old is not null) TryDelete(old, plugin);
    }

    /// <summary>Remove a set-aside folder, its descriptor first: whatever else is left, Unreal must not see a second copy.</summary>
    private static void TryDelete(string dir, string plugin)
    {
        try
        {
            File.Delete(Path.Combine(dir, $"{plugin}.uplugin"));
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    public bool Uninstall(InstallTarget target, string plugin)
    {
        var dirs = new[] { target.PluginDir(plugin), target.LegacyPluginDir(plugin) }
            .OfType<string>().Where(Directory.Exists).ToList();
        if (dirs.Count == 0) { _state.Forget(target, plugin); return false; }
        if (!IsWritable(target.Root))
            throw new UnauthorizedAccessException($"{target.Root} is not writable. Run elevated to uninstall from this engine.");
        foreach (var dir in dirs)
        {
            Directory.Delete(dir, recursive: true);
            _log($"uninstalled: {plugin} from {dir}");
        }
        _state.Forget(target, plugin);
        return true;
    }

    /// <summary>
    /// Move what a hub before 0.4.3 installed into Engine/Plugins/AutomationForge over to the current root,
    /// Engine/Plugins/Marketplace/AutomationForge (see EngineInstall.PluginRoot). Each plugin is one folder
    /// rename on the same volume, so it moves whole or stays where it was; a folder the editor has loaded
    /// stays and is named. Receipts follow their folders. Needs the same rights as an install.
    /// Returns how many plugin folders are still in the legacy root.
    /// </summary>
    public int MoveLegacyEngineInstalls(InstallTarget target)
    {
        if (target.LegacyRoot is null || !Directory.Exists(target.LegacyRoot)) return 0;
        var left = 0;
        foreach (var from in Directory.GetDirectories(target.LegacyRoot))
        {
            var name = Path.GetFileName(from);
            var to = target.PluginDir(name);
            if (Directory.Exists(to))
            {
                _log($"not moved: {name} — {to} already exists, so there are two copies; uninstall it and install it again.");
                left++;
                continue;
            }
            try
            {
                Directory.CreateDirectory(target.Root);
                Directory.Move(from, to);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log($"not moved: {name} — Unreal Editor has it loaded, or the folder is open elsewhere. Close the editor and try again. ({ex.Message})");
                left++;
                continue;
            }
            _state.Moved(target.LegacyRoot, target.Root, name, to);
            _log($"moved: {name} → {to}");
        }
        if (left == 0)
        {
            // Only the now-empty folder is removed; anything else in it (a stray file) keeps it.
            try { Directory.Delete(target.LegacyRoot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return left;
    }

    private async Task<string> DownloadAsync(string url, string plugin, VersionInfo version, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Paths.DownloadDir);
        var file = Path.Combine(Paths.DownloadDir, $"{plugin}-{version.Version}-UE{version.Engine}-{version.Platform}.zip");

        // A previous download with the right hash is reused; anything else is replaced.
        if (File.Exists(file) && version.Sha256 is not null && Sha256Of(file) == version.Sha256)
        {
            _log($"cached: {Path.GetFileName(file)}");
            return file;
        }

        _log($"downloading: {new Uri(url).GetLeftPart(UriPartial.Path)}");
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? version.Size;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(file);
        var buffer = new byte[1 << 16];
        long done = 0; int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (total > 0) progress?.Report((double)done / total);
        }
        return file;
    }

    private static void VerifyChecksum(string file, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return; // the manifest may predate digests; the hub does not invent one
        var actual = Sha256Of(file);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
            throw new InvalidDataException($"Checksum mismatch for {Path.GetFileName(file)}: expected {expected}, got {actual}. The download was discarded.");
        }
    }

    private static string Sha256Of(string file) => Checksums.Sha256Of(file);
}
