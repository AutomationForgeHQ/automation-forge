using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Forge.Hub.ViewModels;

namespace Forge.Hub.Views;

/// <summary>
/// The window owns its chrome: the OS title bar is replaced by a strip of ours
/// (drag, double-click to maximise, our own minimise / maximise / close) while
/// the native resize edges and Windows snap behaviour stay.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            // Maximised windows with no chrome sit a border-width off-screen; pad it back in.
            if (e.Property == WindowStateProperty)
                Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void Scrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ClosePanels();
        e.Handled = true;
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
