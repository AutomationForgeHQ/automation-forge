using Avalonia;
using Avalonia.Headless;

namespace Forge.Hub.Snapshots;

/// <summary>
/// dotnet run --project tests/Forge.Hub.Snapshots -- &lt;out dir&gt; slice0 &lt;loop.webp&gt; &lt;still.jpg&gt;
/// dotnet run --project tests/Forge.Hub.Snapshots -- &lt;out dir&gt; screens
///
/// The Hub's App and its styles, on Avalonia's headless platform drawn by Skia. Nothing appears on the desktop.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: <out dir> slice0 <webp> <still> | screens"); return 2; }
        var outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        switch (args[1])
        {
            case "slice0":
                Slice0.Run(outDir, args[2], args[3]);
                break;
            case "beta3":
                Screens.RunBeta3(outDir, args.Skip(2).ToArray());
                break;
            case "screens":
                Screens.Run(outDir, args.Skip(2).ToArray());
                break;
            default:
                Console.Error.WriteLine($"unknown mode {args[1]}");
                return 2;
        }
        return 0;
    }
}
