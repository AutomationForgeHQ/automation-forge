using Avalonia;
using Avalonia.Controls;

namespace Forge.Hub.Views;

/// <summary>
/// One plugin row: the name (children[0]), the version and status (children[1]) and the buttons (children[2]).
///
/// When the three fit side by side with at least <see cref="MinNameWidth"/> left for the name, it lays them out exactly
/// as 0.4.4's Grid did (ColumnDefinitions="*,Auto,Auto", ColumnSpacing 20): a row that fitted in 0.4.4 looks the same.
/// When they do not (the smallest window with "Buy Full" and "Install Lite", or Remove, What's new and Update), the
/// buttons move to a second line, right-aligned, instead of pushing the name out of the row and the buttons past its
/// edge; the status block is then measured at the width left beside the name, so a licence line can wrap its
/// "Restore licence" under it.
/// </summary>
public sealed class PluginRowPanel : Panel
{
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<PluginRowPanel, double>(nameof(ColumnSpacing), 20);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<PluginRowPanel, double>(nameof(RowSpacing), 10);

    public static readonly StyledProperty<double> MinNameWidthProperty =
        AvaloniaProperty.Register<PluginRowPanel, double>(nameof(MinNameWidth), 64);

    static PluginRowPanel() =>
        AffectsMeasure<PluginRowPanel>(ColumnSpacingProperty, RowSpacingProperty, MinNameWidthProperty);

    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }
    public double MinNameWidth { get => GetValue(MinNameWidthProperty); set => SetValue(MinNameWidthProperty, value); }

    /// <summary>Whether the last measure put the buttons on a line of their own.</summary>
    public bool IsStacked { get; private set; }

    private Control? Name0 => Children.Count > 0 ? Children[0] : null;
    private Control? Status => Children.Count > 1 ? Children[1] : null;
    private Control? Actions => Children.Count > 2 ? Children[2] : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Name0 is not { } name || Status is not { } status || Actions is not { } actions) return base.MeasureOverride(availableSize);
        var gap = ColumnSpacing;
        var width = availableSize.Width;
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        status.Measure(inf);
        actions.Measure(inf);
        var (s, a) = (Visible(status), Visible(actions));
        var statusW = s ? status.DesiredSize.Width : 0;
        var actionsW = a ? actions.DesiredSize.Width : 0;
        var gaps = (s ? gap : 0) + (a ? gap : 0);

        IsStacked = !double.IsInfinity(width) && a && width - statusW - actionsW - gaps < MinNameWidth;
        if (!IsStacked)
        {
            var nameW = double.IsInfinity(width) ? double.PositiveInfinity : Math.Max(0, width - statusW - actionsW - gaps);
            name.Measure(new Size(nameW, availableSize.Height));
            var h = Math.Max(name.DesiredSize.Height, Math.Max(status.DesiredSize.Height, actions.DesiredSize.Height));
            var w = double.IsInfinity(width) ? name.DesiredSize.Width + statusW + actionsW + gaps : width;
            return new Size(w, h);
        }

        // Two lines: the name and the status, then the buttons. The status gets what the name does not need.
        name.Measure(inf);
        var nameWant = Math.Max(MinNameWidth, Math.Min(name.DesiredSize.Width, width * 0.4));
        if (s)
        {
            status.Measure(new Size(Math.Max(0, width - nameWant - gap), double.PositiveInfinity));
            statusW = status.DesiredSize.Width;
        }
        name.Measure(new Size(Math.Max(0, width - statusW - (s ? gap : 0)), availableSize.Height));
        var line1 = Math.Max(name.DesiredSize.Height, s ? status.DesiredSize.Height : 0);
        return new Size(width, line1 + RowSpacing + actions.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Name0 is not { } name || Status is not { } status || Actions is not { } actions) return base.ArrangeOverride(finalSize);
        var gap = ColumnSpacing;
        var width = finalSize.Width;
        var (s, a) = (Visible(status), Visible(actions));
        var statusW = s ? status.DesiredSize.Width : 0;
        var actionsW = a ? actions.DesiredSize.Width : 0;

        if (!IsStacked)
        {
            var h = finalSize.Height;
            var actionsX = width - actionsW;
            var statusX = actionsX - (a ? gap : 0) - statusW;
            var nameW = Math.Max(0, statusX - (s ? gap : 0));
            name.Arrange(new Rect(0, 0, nameW, h));
            status.Arrange(new Rect(statusX, 0, statusW, h));
            actions.Arrange(new Rect(actionsX, 0, actionsW, h));
            return finalSize;
        }

        var line1 = Math.Max(name.DesiredSize.Height, s ? status.DesiredSize.Height : 0);
        var statusLeft = width - statusW;
        name.Arrange(new Rect(0, 0, Math.Max(0, statusLeft - (s ? gap : 0)), line1));
        status.Arrange(new Rect(statusLeft, 0, statusW, line1));
        actions.Arrange(new Rect(width - actionsW, line1 + RowSpacing, actionsW, actions.DesiredSize.Height));
        return finalSize;
    }

    private static bool Visible(Control c) => c.IsVisible;
}
