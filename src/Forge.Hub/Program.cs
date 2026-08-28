using Avalonia;
using System;

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
