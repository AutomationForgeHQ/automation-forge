using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Forge.Hub.Snapshots;

/// <summary>Rendering helpers: a control or a whole window to a PNG, after letting the dispatcher settle.</summary>
internal static class Snap
{
    /// <summary>Run the dispatcher and the render timer for a while, so bindings, layout and async pictures land.</summary>
    public static void Pump(int ms = 400)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        } while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A loose control, laid out at the given size and drawn by Skia into a bitmap.</summary>
    public static void Control(Control control, int width, int height, string file, double scale = 1.0)
    {
        var host = new Window { Width = width, Height = height, Content = control, SizeToContent = SizeToContent.Manual };
        host.Show();
        Pump(300);
        var dpi = 96 * scale;
        using var rtb = new RenderTargetBitmap(new PixelSize((int)(width * scale), (int)(height * scale)), new Vector(dpi, dpi));
        rtb.Render(control);
        rtb.Save(file);
        host.Close();
        Console.WriteLine($"  wrote {file}");
    }

    /// <summary>A whole window as the headless platform composed it.</summary>
    public static void Window(Window window, string file, int settleMs = 900)
    {
        Pump(settleMs);
        var frame = window.CaptureRenderedFrame();
        if (frame is null) { Console.WriteLine($"  NO FRAME for {file}"); return; }
        frame.Save(file);
        Console.WriteLine($"  wrote {file}");
    }
}
