using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LighthouseManager.Tray.Api;
using LighthouseManager.Tray.Integration;
using LighthouseManager.Tray.ViewModels;
using LighthouseManager.Tray.Views;

namespace LighthouseManager.Tray;

public class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();
    private ApiClient? _api;
    private FlyoutWindow? _flyout;
    private TrayIcon? _trayIcon;
    private MainViewModel? _viewModel;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _api = new ApiClient(Program.ApiUrl);
            _viewModel = new MainViewModel(_api, new Updates());
            _flyout = new FlyoutWindow { DataContext = _viewModel };

            _trayIcon = new TrayIcon { Menu = CreateMenu(desktop) };
            _trayIcon.Clicked += (_, _) => ToggleFlyout();
            TrayIcon.SetIcons(this, [_trayIcon]);

            _viewModel.Changed += (_, _) => UpdateTrayIcon();
            UpdateTrayIcon();

            _ = _viewModel.RunEventLoopAsync(_shutdown.Token);
            _ = _viewModel.RunUpdateLoopAsync(_shutdown.Token);
            if (desktop.Args?.Contains("--open") == true) _flyout.ShowFlyout();
            desktop.ShutdownRequested += (_, _) => _shutdown.Cancel();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private NativeMenu CreateMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new NativeMenuItem("Open");
        open.Click += (_, _) => _flyout!.ShowFlyout();
        var wake = new NativeMenuItem("Turn all on") { Command = _viewModel!.WakeAllCommand };
        var sleep = new NativeMenuItem("Sleep all") { Command = _viewModel.SleepAllCommand };
        var settings = new NativeMenuItem("Open settings") { Command = _viewModel.OpenSettingsCommand };
        var restart = new NativeMenuItem("Restart service")
            { Command = _viewModel.RestartServiceCommand, IsVisible = _viewModel.CanManageService };
        // An AppImage has no uninstaller; Windows uninstalls through "Installed apps"
        var remove = new NativeMenuItem("Remove from system…")
            { IsVisible = OperatingSystem.IsLinux() && _viewModel.CanManageService };
        remove.Click += (_, _) =>
        {
            SystemIntegration.Uninstall();
            _shutdown.Cancel();
            _trayIcon?.Dispose();
            desktop.Shutdown();
        };
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) =>
        {
            _shutdown.Cancel();
            _trayIcon?.Dispose();
            desktop.Shutdown();
        };

        return
        [
            open, new NativeMenuItemSeparator(), wake, sleep, new NativeMenuItemSeparator(), settings, restart,
            remove, new NativeMenuItemSeparator(), quit
        ];
    }

    private void ToggleFlyout()
    {
        if (_flyout == null) return;

        if (_flyout.IsVisible) _flyout.HideFlyout();
        // The click that deactivated (hid) the flyout must not reopen it
        else if (DateTime.UtcNow - _flyout.LastHidden > TimeSpan.FromMilliseconds(300)) _flyout.ShowFlyout();
    }

    private void UpdateTrayIcon()
    {
        if (_trayIcon == null || _viewModel == null) return;

        _trayIcon.Icon = TrayIconRenderer.Get(_viewModel.Overall);
        _trayIcon.ToolTipText = TrayToolTip.Build(_viewModel.StatusText,
            _viewModel.Stations.Select(st => (st.DisplayName, st.StateText)).ToList());
    }
}
