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
    private SettingsWindow? _settings;
    private UpdateWindow? _updateNotice;
    private string? _noticeShownFor;
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
            // The update check runs separately from the status events
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.Update) or nameof(MainViewModel.IsUpdating))
                {
                    UpdateMenu();
                    UpdateTrayIcon();
                }

                if (e.PropertyName == nameof(MainViewModel.Update)) ShowUpdateNotice();
            };
            _viewModel.UpdateDismissed += (_, _) => _updateNotice?.Hide();
            _viewModel.SettingsRequested += (_, _) => ShowSettings();
            UpdateTrayIcon();

            _ = _viewModel.RunEventLoopAsync(_shutdown.Token);
            _ = _viewModel.RunUpdateLoopAsync(_shutdown.Token);
            if (desktop.Args?.Contains("--open") == true) _flyout.ShowFlyout();
            // "--settings [stations|triggers|general]" opens the settings window, optionally on a tab
            if (desktop.Args is { } args && Array.IndexOf(args, "--settings") is var index and >= 0)
                ShowSettings(index + 1 < args.Length ? args[index + 1] : null);
            Program.Instance?.ListenForActivation(() =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _flyout.ShowFlyout()));
            desktop.ShutdownRequested += (_, _) => _shutdown.Cancel();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private NativeMenuItem? _updateItem;
    private NativeMenuItemSeparator? _updateSeparator;

    private NativeMenu CreateMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Shown only while an update is available (see UpdateMenu)
        _updateItem = new NativeMenuItem("Update") { Command = _viewModel!.InstallUpdateCommand, IsVisible = false };
        _updateSeparator = new NativeMenuItemSeparator { IsVisible = false };
        var open = new NativeMenuItem("Open");
        open.Click += (_, _) => _flyout!.ShowFlyout();
        var wake = new NativeMenuItem("Turn all on") { Command = _viewModel.WakeAllCommand };
        var sleep = new NativeMenuItem("Sleep all") { Command = _viewModel.SleepAllCommand };
        var settingsWindow = new NativeMenuItem("Settings…");
        settingsWindow.Click += (_, _) => ShowSettings();
        var find = new NativeMenuItem("Find base stations…");
        find.Click += (_, _) =>
        {
            _flyout!.ShowFlyout();
            _viewModel.FindStationsCommand.Execute(null);
        };
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
            _updateItem, _updateSeparator, open, settingsWindow, new NativeMenuItemSeparator(), wake, sleep, new NativeMenuItemSeparator(), find,
            settings, restart,
            remove, new NativeMenuItemSeparator(), quit
        ];
    }

    private void ShowSettings(string? tab = null)
    {
        _flyout?.HideFlyout();
        _settings ??= new SettingsWindow { DataContext = new SettingsViewModel(_viewModel!) };
        _settings.ShowSettings(tab);
    }

    private void ToggleFlyout()
    {
        if (_flyout == null) return;

        if (_flyout.IsVisible) _flyout.HideFlyout();
        // The click that deactivated (hid) the flyout must not reopen it
        else if (DateTime.UtcNow - _flyout.LastHidden > TimeSpan.FromMilliseconds(300)) _flyout.ShowFlyout();
    }

    /// <summary>
    ///     Pops up the update notice once per new version, unless the user chose "Later" for it.
    /// </summary>
    private void ShowUpdateNotice()
    {
        var version = _viewModel?.UpdateVersion;
        if (version == null)
        {
            _updateNotice?.Hide();
            return;
        }

        if (version == _noticeShownFor || _viewModel!.IsUpdateDismissed) return;
        _noticeShownFor = version;
        // The open flyout already shows the update button
        if (_flyout?.IsVisible == true) return;

        _updateNotice ??= new UpdateWindow { DataContext = _viewModel };
        _updateNotice.ShowNotice();
    }

    private void UpdateMenu()
    {
        if (_updateItem == null || _updateSeparator == null || _viewModel == null) return;

        _updateItem.Header = _viewModel.IsUpdating ? "Updating…" : $"{_viewModel.UpdateText}…";
        _updateItem.IsEnabled = !_viewModel.IsUpdating;
        _updateItem.IsVisible = _updateSeparator.IsVisible = _viewModel.IsUpdateAvailable;
    }

    private void UpdateTrayIcon()
    {
        if (_trayIcon == null || _viewModel == null) return;

        _trayIcon.Icon = TrayIconRenderer.Get(_viewModel.Overall, _viewModel.IsUpdateAvailable);
        _trayIcon.ToolTipText = TrayToolTip.Build(_viewModel.StatusText,
            _viewModel.Stations.Select(st => (st.DisplayName, st.StateText)).ToList(),
            _viewModel.Update?.TargetFullRelease.Version.ToString());
    }
}
