using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Forge.Hub.Views.Pages;

public partial class BrowsePageView : UserControl
{
    public BrowsePageView() => InitializeComponent();

    /// <summary>The rail's round arrows move it by one card (210 + 16).</summary>
    private void RailLeft_Click(object? sender, RoutedEventArgs e) => Rail.Offset = new Vector(Math.Max(0, Rail.Offset.X - 226), 0);
    private void RailRight_Click(object? sender, RoutedEventArgs e) => Rail.Offset = new Vector(Rail.Offset.X + 226, 0);
}
