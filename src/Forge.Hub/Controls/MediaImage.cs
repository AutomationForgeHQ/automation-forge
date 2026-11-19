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
/// A still from the catalogue (12.4), by the site's path ("/media/animation/door.jpg?v=..."). In order: a
/// FORGE_MEDIA_URL folder (the website's v2/public, for local work), the disk cache, the installer's set beside
/// the executable, and last a download from the site into the cache (<see cref="MediaCache"/>), so a product
/// that exists only in a fetched catalogue still gets its pictures. Decoded off the UI thread at twice its layout width, kept in a
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

    /// <summary>
    /// Where a site path is on this machine, or null (12.4): a FORGE_MEDIA_URL folder first (local work), then the
    /// disk cache (by the whole path, ?v= stamp included), then the installer's set. Nothing is fetched here.
    /// </summary>
    public static string? Resolve(string? sitePath)
    {
        if (string.IsNullOrWhiteSpace(sitePath)) return null;
        var rel = Catalogue.LocalMediaPath(sitePath).Replace('/', System.IO.Path.DirectorySeparatorChar);
        if (rel.Split(System.IO.Path.DirectorySeparatorChar).Contains("..")) return null;
        if (Environment.GetEnvironmentVariable("FORGE_MEDIA_URL") is { Length: > 0 } root && Directory.Exists(root))
        {
            var dev = System.IO.Path.Combine(root, rel);
            if (File.Exists(dev)) return dev;
        }
        if (MediaCache.Shared.Cached(sitePath) is { } cached) return cached;
        var bundled = System.IO.Path.Combine(CatalogueLoader.BundledMediaRoot, rel);
        return File.Exists(bundled) ? bundled : null;
    }

    /// <summary>
    /// The picture can be shown now (it is on this machine). When it is not, it is fetched in the background, and
    /// when it arrives the page in front is refreshed, so a block that is left out without its picture (a poster,
    /// an interface shot) appears once the picture is here. Offline, it stays out, as before.
    /// </summary>
    public static bool Available(string? sitePath)
    {
        if (Resolve(sitePath) is not null) return true;
        if (string.IsNullOrWhiteSpace(sitePath)) return false;
        _ = FetchThenAnnounce(sitePath);
        return false;
    }

    private static async Task FetchThenAnnounce(string sitePath)
    {
        if (await MediaCache.Shared.GetAsync(sitePath).ConfigureAwait(false) is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.DataContext: ViewModels.MainViewModel vm })
                vm.OnMediaArrived();
        });
    }

    private void Load()
    {
        var path = Path;
        var width = Math.Max(64, DecodeWidth * 2);
        if (string.IsNullOrWhiteSpace(path)) { _loading = null; Source = null; Opacity = 0; return; }
        var key = $"{path}|{width}";
        if (Cache.TryGetValue(key, out var hit) && hit is not null) { _loading = key; Show(hit, animate: false); return; }

        _loading = key;
        Source = null;
        Opacity = 0;
        // Off the UI thread: the cache and the installer's set first, then the site (MediaCache keeps the download
        // bounded, on the site's host and image-only). No picture is simply the plate colour.
        Task.Run(async () =>
        {
            var file = Resolve(path) ?? await MediaCache.Shared.GetAsync(path).ConfigureAwait(false);
            Bitmap? bmp = null;
            if (file is not null)
            {
                try
                {
                    using var s = File.OpenRead(file);
                    bmp = Bitmap.DecodeToWidth(s, width, BitmapInterpolationMode.HighQuality);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { }
            }
            // Only a decoded picture is remembered: a miss may be fetched later, when the network is back.
            if (bmp is not null) Cache[key] = bmp;
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
