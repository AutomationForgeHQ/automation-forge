using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Forge.Core.Catalogue;

namespace Forge.Hub.Controls;

/// <summary>
/// A still from the catalogue (12.4), by the site's path ("/media/animation/door.jpg?v=..."). Slice 1 reads
/// the installer's set beside the executable, or FORGE_MEDIA_URL when it names a folder (the website's
/// v2/public for local work). The picture is decoded off the UI thread at twice its layout width, kept in a
/// small shared cache, and faded in over 0.2 s; until then the card shows its plate colour. A missing file is
/// simply no picture.
/// </summary>
public class MediaImage : Image
{
    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<MediaImage, string?>(nameof(Path));

    /// <summary>The width the picture is shown at, in device-independent pixels. Decoded at twice that.</summary>
    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<MediaImage, int>(nameof(DecodeWidth), 600);

    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();
    private string? _loading;

    static MediaImage()
    {
        StretchProperty.OverrideDefaultValue<MediaImage>(Stretch.UniformToFill);
        PathProperty.Changed.AddClassHandler<MediaImage>((m, _) => m.Load());
        DecodeWidthProperty.Changed.AddClassHandler<MediaImage>((m, _) => m.Load());
    }

    public MediaImage()
    {
        Opacity = 0;
        RenderTransformOrigin = RelativePoint.Center;
        Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromSeconds(0.2) },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromSeconds(0.7), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
        ];
    }

    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    /// <summary>Where a site path is on this machine, or null. FORGE_MEDIA_URL (a folder) first, then the installer's set.</summary>
    public static string? Resolve(string? sitePath)
    {
        if (string.IsNullOrWhiteSpace(sitePath)) return null;
        var rel = Catalogue.LocalMediaPath(sitePath).Replace('/', System.IO.Path.DirectorySeparatorChar);
        if (Environment.GetEnvironmentVariable("FORGE_MEDIA_URL") is { Length: > 0 } root && Directory.Exists(root))
        {
            var dev = System.IO.Path.Combine(root, rel);
            if (File.Exists(dev)) return dev;
        }
        var bundled = System.IO.Path.Combine(CatalogueLoader.BundledMediaRoot, rel);
        return File.Exists(bundled) ? bundled : null;
    }

    private void Load()
    {
        var file = Resolve(Path);
        var width = Math.Max(64, DecodeWidth * 2);
        var key = $"{file}|{width}";
        if (file is null) { Source = null; Opacity = 0; return; }
        if (Cache.TryGetValue(key, out var hit)) { Show(hit, animate: false); return; }

        _loading = key;
        Task.Run(() =>
        {
            Bitmap? bmp = null;
            try
            {
                using var s = File.OpenRead(file);
                bmp = Bitmap.DecodeToWidth(s, width, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
            Cache[key] = bmp;
            Dispatcher.UIThread.Post(() => { if (_loading == key) Show(bmp, animate: true); });
        });
    }

    private void Show(Bitmap? bmp, bool animate)
    {
        Source = bmp;
        if (!animate)
        {
            var t = Transitions;
            Transitions = null;
            Opacity = bmp is null ? 0 : 1;
            Transitions = t;
        }
        else Opacity = bmp is null ? 0 : 1;
    }
}
