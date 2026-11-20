using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Forge.Hub.Controls;

/// <summary>
/// A picture from a file on this machine (a project's thumbnail), decoded off the UI thread to twice the width it
/// is shown at, kept in a small shared cache by path, size and the file's time, and faded in. Until it is decoded,
/// and for good when the file is missing or does not decode, nothing is drawn: what is under it (the tile's
/// initials) shows through.
/// </summary>
public class FileImage : Image
{
    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<FileImage, string?>(nameof(Path));

    /// <summary>The width the picture is shown at, in device-independent pixels. Decoded at twice that.</summary>
    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<FileImage, int>(nameof(DecodeWidth), 240);

    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new();
    private string? _loading;

    static FileImage()
    {
        StretchProperty.OverrideDefaultValue<FileImage>(Stretch.UniformToFill);
        PathProperty.Changed.AddClassHandler<FileImage>((m, _) => m.Load());
        DecodeWidthProperty.Changed.AddClassHandler<FileImage>((m, _) => m.Load());
    }

    public FileImage()
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

    private void Load()
    {
        var path = Path;
        var width = Math.Max(64, DecodeWidth * 2);
        if (string.IsNullOrWhiteSpace(path)) { _loading = null; Source = null; Opacity = 0; return; }
        var request = $"{path}|{width}";
        _loading = request;
        Source = null;
        Opacity = 0;
        Task.Run(() =>
        {
            Bitmap? bmp = null;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    var key = $"{request}|{info.LastWriteTimeUtc.Ticks}";
                    if (!Cache.TryGetValue(key, out bmp))
                    {
                        using var s = info.OpenRead();
                        bmp = Bitmap.DecodeToWidth(s, width, BitmapInterpolationMode.HighQuality);
                        if (Cache.Count > 200) Cache.Clear();
                        Cache[key] = bmp;
                    }
                }
            }
            // A file that is locked, half-written or not a picture is simply no picture. The decoder's own failure on
            // a broken file is not one documented type, and a picture is never worth more than falling back quietly.
            catch (Exception) { bmp = null; }
            Dispatcher.UIThread.Post(() =>
            {
                if (_loading != request) return;
                Source = bmp;
                Opacity = bmp is null ? 0 : 1;
            });
        });
    }
}
