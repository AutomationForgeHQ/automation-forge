using Avalonia.Controls;
using Avalonia.Input;
using Forge.Hub.ViewModels.Pages;

namespace Forge.Hub.Views.Pages;

public partial class ProductPageView : UserControl
{
    public ProductPageView()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (DataContext is not ProductPageViewModel vm || !vm.ShowViewer) return;
            if (e.Key == Key.Escape) { vm.CloseViewerCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Right) { vm.NextShotCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Left) { vm.PreviousShotCommand.Execute(null); e.Handled = true; }
        };
    }

    /// <summary>A click on the scrim, not on the picture or a button, closes the viewer.</summary>
    private void Viewer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender && DataContext is ProductPageViewModel vm) vm.CloseViewerCommand.Execute(null);
    }
}
