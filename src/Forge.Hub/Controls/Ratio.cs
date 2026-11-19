using Avalonia;
using Avalonia.Controls;

namespace Forge.Hub.Controls;

/// <summary>
/// Keeps its child at a fixed height-to-width ratio (spec 3: cards in a row share the width equally and keep
/// their ratio). A track door is 4:5, so Ratio="1.25".
/// </summary>
public class Ratio : Decorator
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Ratio, double>(nameof(Value), 1.25);

    static Ratio() => AffectsMeasure<Ratio>(ValueProperty);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 264 : availableSize.Width;
        var size = new Size(w, w * Value);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = new Size(finalSize.Width, finalSize.Width * Value);
        Child?.Arrange(new Rect(size));
        return size;
    }
}
