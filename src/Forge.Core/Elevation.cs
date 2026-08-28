using System.Diagnostics;

namespace Forge.Core;

/// <summary>
/// A launcher engine lives under Program Files. Rather than failing halfway
/// through, a command that needs to write there relaunches itself elevated —
/// one UAC prompt, the way the Epic launcher does it — and waits for the result.
/// </summary>
public static class Elevation
{
    public const string NoElevateFlag = "--no-elevate";

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return Environment.UserName == "root";
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Re-run the current process elevated with the same arguments. Returns its exit code, or null if it could not be launched.</summary>
    public static int? RelaunchElevated(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the executable to relaunch.");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(NoElevateFlag);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // the UAC prompt was declined
        }
    }
}
