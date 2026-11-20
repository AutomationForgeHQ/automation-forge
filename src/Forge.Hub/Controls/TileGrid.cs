using Avalonia;
using Avalonia.Controls;

namespace Forge.Hub.Controls;

/// <summary>
/// Tiles in rows that reflow with the window, as many to a row as fit at <see cref="MinItemWidth"/>, then widened
/// together so the row ends on the page's right edge (a WrapPanel leaves a ragged gap there). Every tile in a row
/// gets the row's tallest height, so tiles of one row are always the same size.
/// </summary>
public class TileGrid : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<TileGrid, double>(nameof(MinItemWidth), 230);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<TileGrid, double>(nameof(Spacing), 20);

    static TileGrid() => AffectsMeasure<TileGrid>(MinItemWidthProperty, SpacingProperty);

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private (int Columns, double ItemWidth) Layout(double width)
    {
        var s = Spacing;
        var min = Math.Max(1, MinItemWidth);
        if (double.IsInfinity(width) || double.IsNaN(width)) width = min * 4 + s * 3;
        var columns = Math.Max(1, (int)Math.Floor((width + s) / (min + s)));
        var item = Math.Max(0, (width - s * (columns - 1)) / columns);
        return (columns, item);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, item) = Layout(availableSize.Width);
        var visible = Children.Where(c => c.IsVisible).ToList();
        var height = 0.0;
        for (var row = 0; row * columns < visible.Count; row++)
        {
            var tallest = 0.0;
            foreach (var child in visible.Skip(row * columns).Take(columns))
            {
                child.Measure(new Size(item, double.PositiveInfinity));
                tallest = Math.Max(tallest, child.DesiredSize.Height);
            }
            height += tallest + (row > 0 ? Spacing : 0);
        }
        var width = double.IsInfinity(availableSize.Width) ? columns * item + (columns - 1) * Spacing : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, item) = Layout(finalSize.Width);
        var visible = Children.Where(c => c.IsVisible).ToList();
        var y = 0.0;
        for (var row = 0; row * columns < visible.Count; row++)
        {
            var cells = visible.Skip(row * columns).Take(columns).ToList();
            var tallest = cells.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < cells.Count; i++)
                cells[i].Arrange(new Rect(i * (item + Spacing), y, item, tallest));
            y += tallest + Spacing;
        }
        return finalSize;
    }
}
