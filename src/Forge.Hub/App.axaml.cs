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
            Toasts.OnActivated(() => Dispatcher.UIThread.Post(ShowWindow));

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
        if (_quitting || e.CloseReason == WindowCloseReason.ApplicationShutdown || !_settings.RunInBackground) return;
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
        var open = new NativeMenuItem("Open Automation Forge");
        open.Click += (_, _) => ShowWindow();
        var check = new NativeMenuItem("Check for updates now");
        check.Click += async (_, _) =>
        {
            var r = await _watcher.CheckAsync();
            if (r is null) Toasts.Show("Automation Forge", "Could not reach the releases. The last known state stands.");
            else if (r.Count == 0) Toasts.Show("Automation Forge", "Everything is current.");
            else Announce(r, force: true);
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
            ToolTipText = "Automation Forge",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [_tray]);
    }

    private void OnChecked(WatchResult r)
    {
        if (_tray is null) return;
        _tray.ToolTipText = r.Count switch
        {
            0 => "Automation Forge — up to date",
            1 => "Automation Forge — 1 update",
            var n => $"Automation Forge — {n} updates",
        };
        if (_window?.DataContext is MainViewModel vm) _ = vm.RefreshQuietlyAsync();
    }

    private void Announce(WatchResult r, bool force)
    {
        if (!force && !_settings.NotifyOnUpdates) return;
        var names = r.Plugins.GroupBy(p => p.Plugin).Select(g => $"{g.Key} {g.First().To}").ToList();
        if (r.Hub is { } hub) names.Add($"hub {hub.Version}");
        if (names.Count == 0) return;
        var title = names.Count == 1 ? "1 update available" : $"{names.Count} updates available";
        var body = string.Join(" · ", names.Take(4)) + (names.Count > 4 ? $" · and {names.Count - 4} more" : "");
        Toasts.Show(title, body);
    }
}
