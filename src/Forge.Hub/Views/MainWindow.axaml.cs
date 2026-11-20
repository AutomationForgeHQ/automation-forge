using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Forge.Hub.ViewModels;

namespace Forge.Hub.Views;

/// <summary>
/// The window owns its chrome: the OS title bar is replaced by the bar of spec 3 (drag on any empty part,
/// double-click to maximise, our own minimise / maximise / close) while the native resize edges and Windows
/// snap behaviour stay. Esc closes a drawer; Alt+Left and the mouse's back button go back.
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
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnBackButton, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape && vm.ShowAnyPanel) { vm.ClosePanels(); e.Handled = true; }
        else if (e.Key == Key.Escape && vm.ShowSearch) { vm.ClearSearch(); e.Handled = true; }
        else if (e.Key == Key.Escape && vm.CurrentPage?.BackInside() == true) e.Handled = true;
        else if (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt && Back(vm)) e.Handled = true;
    }

    private void OnBackButton(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed && Vm is { } vm && Back(vm)) e.Handled = true;
    }

    /// <summary>Back: out of the page's own state first (a project opened on the Projects page), then the page before.</summary>
    private static bool Back(MainViewModel vm)
    {
        if (vm.CurrentPage?.BackInside() == true) return true;
        if (!vm.CanGoBack) return false;
        vm.GoBack();
        return true;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void Scrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Vm?.ClosePanels();
        e.Handled = true;
    }

    /// <summary>A row of the account menu or the gear menu was chosen: the menu closes.</summary>
    private void MenuRow_Click(object? sender, RoutedEventArgs e)
    {
        // After the row's command has run, not before: Click is raised ahead of the command, and closing the
        // menu here takes the row out of the tree, so its command binding is gone by the time the button reads it.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AvatarButton.Flyout?.Hide();
            GearButton.Flyout?.Hide();
        });
    }

    /// <summary>Enter opens the first result; Esc empties the field.</summary>
    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.Enter) { vm.OpenFirstResult(); e.Handled = true; }
        else if (e.Key == Key.Escape) { vm.ClearSearch(); e.Handled = true; }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
