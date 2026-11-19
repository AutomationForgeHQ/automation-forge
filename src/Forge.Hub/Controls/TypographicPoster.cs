using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Forge.Hub.Controls;

/// <summary>
/// The poster of a product with no picture (11.4): a gradient from #16181C to ink, a 28 px grid of bone at
/// 4.5 %, an amber glow at 30 % from a corner chosen by the slug, and the product's mark (or its name) large
/// in the wide cut, outlined in amber at 55 %.
/// </summary>
public class TypographicPoster : Control
{
    public static readonly StyledProperty<string?> MarkProperty = AvaloniaProperty.Register<TypographicPoster, string?>(nameof(Mark));
    public static readonly StyledProperty<string?> SlugProperty = AvaloniaProperty.Register<TypographicPoster, string?>(nameof(Slug));

    static TypographicPoster() => AffectsRender<TypographicPoster>(MarkProperty, SlugProperty);

    public string? Mark { get => GetValue(MarkProperty); set => SetValue(MarkProperty, value); }
    public string? Slug { get => GetValue(SlugProperty); set => SetValue(SlugProperty, value); }

    public override void Render(DrawingContext context)
    {
        var r = new Rect(Bounds.Size);
        if (r.Width <= 0 || r.Height <= 0) return;

        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#16181C"), 0), new GradientStop(Color.Parse("#08090A"), 1) },
        }, r);

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x0B, 0xF1, 0xF0, 0xEC)), 1);
        for (double x = 28; x < r.Width; x += 28) context.DrawLine(grid, new Point(x, 0), new Point(x, r.Height));
        for (double y = 28; y < r.Height; y += 28) context.DrawLine(grid, new Point(0, y), new Point(r.Width, y));

        // The corner of the glow follows the slug, so neighbouring posters differ.
        var corner = (Slug ?? "").Aggregate(0, (h, c) => h * 31 + c) & 3;
        var center = new RelativePoint(corner % 2 == 0 ? 0.1 : 0.9, corner < 2 ? 0.1 : 0.9, RelativeUnit.Relative);
        context.FillRectangle(new RadialGradientBrush
        {
            Center = center,
            GradientOrigin = center,
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x4D, 0xEB, 0xAA, 0x28), 0), new GradientStop(Color.FromArgb(0, 0xEB, 0xAA, 0x28), 1) },
        }, r);

        var word = string.IsNullOrWhiteSpace(Mark) ? "" : Mark!;
        if (word.Length == 0) return;
        var family = Application.Current?.Resources.TryGetResource("WideFont", null, out var f) == true && f is FontFamily ff ? ff : FontFamily.Default;
        var typeface = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold);
        var size = Math.Min(r.Height * 0.32, r.Width * 1.5 / Math.Max(3, word.Length));
        var text = new FormattedText(word, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Transparent);
        // Long names shrink to fit the frame rather than run off it.
        if (text.Width > r.Width * 0.84)
        {
            size *= r.Width * 0.84 / text.Width;
            text = new FormattedText(word, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Transparent);
        }
        var origin = new Point(r.Width * 0.08, r.Height * 0.12);
        if (text.BuildGeometry(origin) is { } geometry)
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x8C, 0xEB, 0xAA, 0x28)), 1.2), geometry);
    }
}
