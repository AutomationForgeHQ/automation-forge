using System.Reflection;

namespace Forge.Core;

/// <summary>What is running: the version stamped into the entry assembly at build time (VERSION, or CI's -p:Version).</summary>
public static class AppInfo
{
    public static string Version { get; } = Compute();

    public static SemVer SemVer => SemVer.TryParse(Version, out var v) ? v : new SemVer(0, 0, 0, null);

    public static string UserAgent(string surface) => $"forge-{surface}/{Version}";

    private static string Compute()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) info = asm.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
