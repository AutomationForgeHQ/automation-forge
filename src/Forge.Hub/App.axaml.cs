using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Forge.Core;
using Forge.Hub.ViewModels;
using Forge.Hub.Views;

namespace Forge.Hub;

/// <summary>
/// The hub is resident. It keeps an icon by the clock, watches for updates
/// every few hours whether or not its window is open, and raises a system
/// notification when something new appears. Closing the window hides it
/// (unless the person turned that off); Quit lives in the tray menu.
/// </summary>
public partial class App : Application
{
    /// <summary>Set by --tray: come up in the tray with no window.</summary>
    public static bool StartHidden { get; set; }

    /// <summary>Set by --engine: the engine (version or path) to select first.</summary>
    public static string? PreferredEngine { get; set; }

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Settings _settings = null!;
    private UpdateWatcher _watcher = null!;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => _quitting = true;

            _settings = Settings.Load();
            _watcher = new UpdateWatcher(_settings);
            _watcher.Checked += r => Dispatcher.UIThread.Post(() => OnChecked(r));
            _watcher.NewFindings += (r, _) => Dispatcher.UIThread.Post(() => Announce(r, force: false));

            SetupTray();
            SingleInstance.ListenForShow(() => Dispatcher.UIThread.Post(ShowWindow));
            SingleInstance.ListenForQuit(() => Dispatcher.UIThread.Post(Quit));
            Toasts.OnActivated(() => Dispatcher.UIThread.Post(ShowUpdates));

            if (!(StartHidden && _settings.RunInBackground)) ShowWindow();
            _watcher.Start(StartHidden ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(60));
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void ShowWindow()
    {
        if (_desktop is null) return;
        if (_window is null)
        {
            _window = new MainWindow { DataContext = new MainViewModel(_settings, _watcher) };
            _window.Closing += OnWindowClosing;
            _window.Closed += (_, _) =>
            {
                _window = null;
                if (!_settings.RunInBackground || _quitting) Quit();
            };
            _desktop.MainWindow = _window;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Our own X hides to the tray; the application, the OS or an installer closing us is a real close.
        if (_quitting || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown || !_settings.RunInBackground) return;
        e.Cancel = true;
        _window?.Hide();
    }

    public void Quit()
    {
        _quitting = true;
        _watcher.Dispose();
        if (_tray is not null) _tray.IsVisible = false;
        _desktop?.Shutdown();
    }

    private void SetupTray()
    {
        var open = new NativeMenuItem("Open Kovati Hub");
        open.Click += (_, _) => ShowWindow();
        var check = new NativeMenuItem("Check for updates now");
        check.Click += async (_, _) =>
        {
            var r = await _watcher.CheckAsync();
            if (r is null) Toasts.Show("Kovati Hub", "Could not check for updates. Nothing changed.");
            else if (r.Count == 0) Toasts.Show("Kovati Hub", "Everything is up to date.");
            else { Announce(r, force: true); ShowUpdates(); }
        };
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(check);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AutomationForgeHub/Assets/forge.ico"))),
            ToolTipText = "Kovati Hub",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [_tray]);
    }

    private void OnChecked(WatchResult r)
    {
        if (_tray is null) return;
        _tray.ToolTipText = Updates(r).Count switch
        {
            0 => "Kovati Hub: up to date",
            1 => "Kovati Hub: 1 update",
            var n => $"Kovati Hub: {n} updates",
        };
        if (_window?.DataContext is MainViewModel vm) _ = vm.RefreshQuietlyAsync();
    }

    private void Announce(WatchResult r, bool force)
    {
        if (!force && !_settings.NotifyOnUpdates) return;
        var names = Updates(r);
        if (names.Count == 0) return;
        var title = names.Count == 1 ? "1 update available" : $"{names.Count} updates available";
        var body = string.Join(" · ", names.Take(4)) + (names.Count > 4 ? $" · and {names.Count - 4} more" : "");
        Toasts.Show(title, body);
    }
    /// <summary>The window on Library, Updates: where a notification or a tray check that found something lands (8.1).</summary>
    public void ShowUpdates()
    {
        ShowWindow();
        if (_window?.DataContext is MainViewModel vm) vm.ShowUpdates();
    }

    /// <summary>
    /// Updates counted per product, not per component (8.1): "MotionForge 0.4.2 · SpeechForge 0.2.4 · Kovati Hub
    /// 0.5.1". Across every target, Labs included, pinned components left out. A component the catalogue does not
    /// know is named by its id.
    /// </summary>
    private List<string> Updates(WatchResult r)
    {
        _catalogue ??= Core.Catalogue.CatalogueLoader.Load();
        var pins = Core.HubState.Load().Pins;
        var names = r.Plugins
            .Where(p => !pins.ContainsKey(p.Plugin))
            .Select(p =>
            {
                var product = _catalogue?.ProductOfComponent(p.Plugin);
                // An engine counts with the tool it extends.
                if (product is { IsEngine: true } && _catalogue?.Product(product.Extends) is { } parent) product = parent;
                var main = product?.MainComponent ?? p.Plugin;
                var to = r.Plugins.FirstOrDefault(x => string.Equals(x.Plugin, main, StringComparison.OrdinalIgnoreCase))?.To ?? p.To;
                return product is null ? $"{p.Plugin} {p.To}" : $"{product.Name} {to}";
            })
            .Distinct()
            .ToList();
        if (r.Hub is { } hub) names.Add($"Kovati Hub {hub.Version}");
        return names;
    }

    private Core.Catalogue.Catalogue? _catalogue;
}