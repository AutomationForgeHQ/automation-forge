using System.Diagnostics;
using Forge.Core;
using Forge.Core.Releases;
using Microsoft.Win32;

namespace Forge.Hub;

/// <summary>
/// How the hub replaces itself. The installer for the newer release is
/// downloaded, checked against the release's own digest, and run silently with
/// /LAUNCH=1; the hub exits the moment it starts, and the installer brings the
/// new one up. A hub that was not put there by that installer — run from a
/// folder, or from source — is pointed at the release page instead.
/// </summary>
public static class HubUpdater
{
    /// <summary>Must match AppId in installer/AutomationForge.iss.</summary>
    public const string AppId = "{4F0B9C7E-2A6D-4B1F-9E3C-7D5A1F0C8B21}";

    public static string? InstallDir { get; } = FindInstallDir();

    public static bool IsInstalled =>
        InstallDir is not null
        && Environment.ProcessPath is { } exe
        && string.Equals(Path.GetDirectoryName(exe)?.TrimEnd('\\', '/'), InstallDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string? FindInstallDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}_is1");
            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    public static async Task<string> DownloadAsync(HttpClient http, HubRelease release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (release.InstallerUrl is null) throw new InvalidOperationException($"{release.Tag} has no installer.");
        Directory.CreateDirectory(Paths.DownloadDir);
        var file = Path.Combine(Paths.DownloadDir, $"AutomationForge-Setup-{release.Version}.exe");

        if (!(File.Exists(file) && release.InstallerSha256 is not null && Checksums.Matches(file, release.InstallerSha256)))
        {
            using var resp = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? release.InstallerSize;
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
        }
        if (!Checksums.Matches(file, release.InstallerSha256))
        {
            File.Delete(file);
            throw new InvalidDataException("The downloaded installer did not match its published checksum and was discarded.");
        }
        return file;
    }

    /// <summary>Start the installer silently; it relaunches the hub when it is done. The caller exits right after.</summary>
    public static void RunInstaller(string file)
    {
        var psi = new ProcessStartInfo { FileName = file, UseShellExecute = true };
        foreach (var a in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LAUNCH=1" }) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    public static void OpenInBrowser(string url) =>
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
}
