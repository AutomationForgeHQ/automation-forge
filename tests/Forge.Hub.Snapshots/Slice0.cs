using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Forge.Hub.Snapshots;

/// <summary>The three short technical tests of slice 0 (spec 14.2). Each writes a PNG and prints its answer.</summary>
internal static class Slice0
{
    public static void Run(string outDir, string webp, string still)
    {
        FontStretch(outDir);
        CardClip(outDir, still);
        WebP(outDir, webp);
    }

    /// <summary>1. Does FontStretch="SemiExpanded" (and FontWeight) drive the embedded variable Archivo?</summary>
    private static void FontStretch(string outDir)
    {
        const string text = "What do you want to make?";
        var family = (FontFamily)Application.Current!.Resources["DisplayFont"]!;
        double Measure(FontStretch stretch, FontWeight weight)
        {
            var tb = new TextBlock { Text = text, FontFamily = family, FontSize = 40, FontStretch = stretch, FontWeight = weight };
            tb.Measure(Size.Infinity);
            return tb.DesiredSize.Width;
        }

        var normal = Measure(Avalonia.Media.FontStretch.Normal, FontWeight.SemiBold);
        var wide = Measure(Avalonia.Media.FontStretch.SemiExpanded, FontWeight.SemiBold);
        var regular = Measure(Avalonia.Media.FontStretch.Normal, FontWeight.Regular);
        var display = (FontFamily)Application.Current!.Resources["WideFont"]!;
        var tbWide = new TextBlock { Text = text, FontFamily = display, FontSize = 40, FontWeight = FontWeight.SemiBold };
        tbWide.Measure(Size.Infinity);

        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(24), Background = Brushes.Black };
        foreach (var (label, stretch, weight, fam) in new[]
                 {
                     ("Archivo · Normal · SemiBold", Avalonia.Media.FontStretch.Normal, FontWeight.SemiBold, family),
                     ("Archivo · SemiExpanded · SemiBold", Avalonia.Media.FontStretch.SemiExpanded, FontWeight.SemiBold, family),
                     ("Archivo · Normal · Regular", Avalonia.Media.FontStretch.Normal, FontWeight.Regular, family),
                     ("static cut ArchivoDisplay (wdth 115, wght 600)", Avalonia.Media.FontStretch.Normal, FontWeight.SemiBold, display),
                 })
        {
            panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Gray, FontSize = 12 });
            panel.Children.Add(new TextBlock { Text = text, FontFamily = fam, FontSize = 40, FontStretch = stretch, FontWeight = weight, Foreground = Brushes.White });
        }
        Snap.Control(panel, 900, 420, Path.Combine(outDir, "hub_slice0_1_fontstretch.png"));

        Console.WriteLine($"[slice0 1] width at 40px: Normal SemiBold {normal:0.0}, SemiExpanded {wide:0.0}, Normal Regular {regular:0.0}, static wide cut {tbWide.DesiredSize.Width:0.0}");
        Console.WriteLine($"[slice0 1] after the fallback (static cuts embedded as Archivo Regular/Medium/SemiBold): Regular {regular:0.0} vs SemiBold {normal:0.0}");
        Console.WriteLine($"[slice0 1] FontStretch widens the variable font: {(wide > normal * 1.02 ? "YES" : "NO")}; FontWeight drives wght: {(Math.Abs(regular - normal) > 1 ? "YES" : "NO")}");
    }

    /// <summary>2. A 264x330 card, radius 16, ClipToBounds, the picture at 1.04 with an amber glow: clean corners at 100 % and 150 %?</summary>
    private static void CardClip(string outDir, string still)
    {
        // The build the Hub uses: the picture grows by a negative margin (a layout change, not a render
        // transform), clipped by the rounded Border; the ring is a separate Border drawn over the picture,
        // and the glow sits on an outer Border that does not clip.
        Control Card(bool transform)
        {
            var image = new Image { Source = new Bitmap(still), Stretch = Stretch.UniformToFill };
            if (transform) { image.RenderTransform = new ScaleTransform(1.04, 1.04); image.RenderTransformOrigin = RelativePoint.Center; }
            else image.Margin = new Thickness(-5.3, -6.6);
            var clip = new Border { CornerRadius = new CornerRadius(16), ClipToBounds = true, Child = image };
            var ring = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#73EBAA28")) };
            var card = new Border
            {
                Width = 264, Height = 330, CornerRadius = new CornerRadius(16),
                BoxShadow = BoxShadows.Parse("0 40 100 -40 #8CEBAA28"),
                Child = new Panel { Children = { clip, ring } },
            };
            return new Border { Background = new SolidColorBrush(Color.Parse("#08090A")), Padding = new Thickness(40, 30, 40, 70), Child = card };
        }
        Snap.Control(Card(false), 344, 430, Path.Combine(outDir, "hub_slice0_2_card_100.png"), scale: 1.0);
        // 150 %: the whole tree under a 1.5 scale, which is what a window at 150 % display scaling applies at
        // its root. (RenderTargetBitmap with a 144 dpi vector draws clips and transforms at mixed scales in
        // headless; that is a property of the off-screen bitmap, not of a window, so it is not used here.)
        Control Scaled(Control c) => new LayoutTransformControl { LayoutTransform = new ScaleTransform(1.5, 1.5), Child = c };
        Snap.Control(Scaled(Card(false)), 516, 645, Path.Combine(outDir, "hub_slice0_2_card_150.png"));
        Snap.Control(Scaled(Card(true)), 516, 645, Path.Combine(outDir, "hub_slice0_2_card_150_rendertransform.png"));
        Console.WriteLine("[slice0 2] rendered at 100 % and 150 %: look at the corners in hub_slice0_2_card_*.png");
    }

    /// <summary>3. SKCodec on an animated WebP: frame count, frame times, one frame drawn into an Avalonia bitmap.</summary>
    private static void WebP(string outDir, string webp)
    {
        using var codec = SKCodec.Create(webp);
        if (codec is null) { Console.WriteLine("[slice0 3] SKCodec could not open the WebP: NO"); return; }
        var frames = codec.FrameInfo;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        var pick = Math.Min(frames.Length - 1, frames.Length / 2);
        // Decode up to the chosen frame, each onto the previous one as its required frame says.
        for (var i = 0; i <= pick; i++)
        {
            var opts = new SKCodecOptions(i, frames[i].RequiredFrame);
            var r = codec.GetPixels(info, bmp.GetPixels(), opts);
            if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) { Console.WriteLine($"[slice0 3] frame {i}: {r}"); return; }
        }
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
        using var ms = new MemoryStream(data.ToArray());
        var avaloniaBitmap = new Bitmap(ms);
        var image = new Image { Source = avaloniaBitmap, Width = info.Width, Height = info.Height };
        Snap.Control(image, info.Width, info.Height, Path.Combine(outDir, "hub_slice0_3_webp_frame.png"));
        var durations = frames.Select(f => f.Duration).Distinct().ToList();
        Console.WriteLine($"[slice0 3] {codec.EncodedFormat}, {info.Width}x{info.Height}, {frames.Length} frames, durations {string.Join("/", durations)} ms, total {frames.Sum(f => f.Duration)} ms; frame {pick} drawn: YES");
    }
}
