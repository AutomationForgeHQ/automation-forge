using Avalonia;
using System;
using System.Linq;
using System.Threading;
using Forge.Core;

namespace Forge.Hub;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // The hub is its own elevated helper: relaunched with --elevated it does
        // one install or uninstall headlessly and exits, so a Program Files
        // engine needs one UAC prompt and no second executable.
        if (args.Length > 0 && args[0] == Elevated.Flag)
            return Elevated.Run(args[1..]);

        // A windowed app has no console to print to; the version lands in a file CI can read.
        if (args.Contains("--version"))
        {
            System.IO.File.WriteAllText("hub-version.txt", AppInfo.Version);
            return 0;
        }

        // The uninstaller asks us to take our notification registration with us.
        if (args.Contains("--uninstall-toasts"))
        {
            Toasts.Uninstall();
            return 0;
        }

        // The installer, before it replaces the files: ask the running hub to quit and wait for it.
        if (args.Contains("--quit"))
        {
            SingleInstance.SignalQuit();
            return SingleInstance.WaitForExit(TimeSpan.FromSeconds(10)) ? 0 : 1;
        }

        // One hub per session: a second launch raises the first one's window and leaves.
        using var mutex = new Mutex(true, SingleInstance.MutexName, out var first);
        if (!first)
        {
            SingleInstance.SignalShow();
            return 0;
        }

        App.StartHidden = args.Contains("--tray");
        // The editor plugin passes the engine it runs from; the hub opens on that one.
        var engineAt = Array.IndexOf(args, "--engine");
        if (engineAt >= 0 && engineAt + 1 < args.Length) App.PreferredEngine = args[engineAt + 1];
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
