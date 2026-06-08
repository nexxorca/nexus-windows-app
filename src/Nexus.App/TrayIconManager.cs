using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Hardcodet.Wpf.TaskbarNotification;

using Nexus.Core.Models;
using Nexus.Core.Services;
using Nexus.Sync.Models;
using Nexus.Sync.Services;

namespace Nexus.App;

public class TrayIconManager : IDisposable {
    private readonly TaskbarIcon _trayIcon;
    private readonly DispatcherTimer _timer;
    private readonly AppConfig _config;
    private readonly SyncEngine _syncEngine;
    private readonly NexusApiClient _api;
    private readonly ActivityLogService _activity;
    private readonly LogService _log;
    private readonly App _app;
    private string _userName = "";
    private Action _onSyncStarted;
    private Action<SyncResult> _onSyncCompleted;

    public TrayIconManager(
        AppConfig config, SyncEngine syncEngine, NexusApiClient api,
        ActivityLogService activity, LogService log, App app
    ) {
        _config = config;
        _syncEngine = syncEngine;
        _api = api;
        _activity = activity;
        _log = log;
        _app = app;
        _userName = config.UserName ?? "";

        _trayIcon = new TaskbarIcon {
            IconSource = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Resources/nexus.ico")),
            ToolTipText = "Nexus Desktop — Starting...",
            ContextMenu = BuildContextMenu(),
            Visibility = Visibility.Visible
        };

        var ui = Application.Current.Dispatcher;
        _onSyncStarted   = () => ui.Invoke(UpdateTooltip);
        _onSyncCompleted = _  => ui.Invoke(UpdateTooltip);
        _syncEngine.SyncStarted   += _onSyncStarted;
        _syncEngine.SyncCompleted += _onSyncCompleted;

        _timer = new DispatcherTimer {
            Interval = TimeSpan.FromSeconds(config.SyncIntervalSeconds)
        };
        _timer.Tick += async ( s, e ) => {
            if ( ! _config.IsLoggedIn ) return;
            await _syncEngine.RunSync();
        };

        UpdateTooltip();
    }

    public void StartSync() {
        _timer.Interval = TimeSpan.FromSeconds(_config.SyncIntervalSeconds);
        _timer.Start();
        UpdateTooltip();
    }

    public void StopSync() {
        _timer.Stop();
    }

    public void UpdateUserName( string name ) {
        _userName = name;
        _trayIcon.ContextMenu = BuildContextMenu();
        UpdateTooltip();
    }

    private ContextMenu BuildContextMenu() {
        var menu = new ContextMenu();

        var header = new MenuItem {
            Header = string.IsNullOrEmpty(_userName)
                ? "Nexus Desktop"
                : $"Nexus Desktop ({_userName})",
            IsEnabled = false,
            FontWeight = FontWeights.Bold
        };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());

        if ( _config.IsLoggedIn ) {
            var openNexus = new MenuItem { Header = "Open Nexus" };
            openNexus.Click += ( s, e ) => _app.ShowMainWindow();
            menu.Items.Add(openNexus);
        }

        var syncNow = new MenuItem { Header = "Sync Now" };
        syncNow.Click += async ( s, e ) => await _app.TriggerSync();
        menu.Items.Add(syncNow);

        var activityLog = new MenuItem { Header = "Activity Log" };
        activityLog.Click += ( s, e ) => _app.ShowActivityLogWindow();
        menu.Items.Add(activityLog);

        var settings = new MenuItem { Header = "Settings" };
        settings.Click += ( s, e ) => _app.ShowSettingsWindow();
        menu.Items.Add(settings);

        var checkUpdates = new MenuItem { Header = "Check for Updates" };
        checkUpdates.Click += ( s, e ) => _app.TriggerUpdateCheck();
        menu.Items.Add(checkUpdates);

        menu.Items.Add(new Separator());

        if ( _config.IsLoggedIn ) {
            var logout = new MenuItem { Header = "Logout" };
            logout.Click += ( s, e ) => _ = _app.Logout();
            menu.Items.Add(logout);
        }

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += ( s, e ) => {
            _app.PrepareForShutdown();
            _app.Shutdown();
        };
        menu.Items.Add(exit);

        return menu;
    }

    private void UpdateTooltip() {
        string status;
        if ( ! _config.IsLoggedIn ) {
            status = "Nexus Desktop — Not logged in";
        } else if ( _syncEngine.IsRunning ) {
            status = "Nexus Desktop — Syncing...";
        } else if ( _syncEngine.LastSync is { } last ) {
            var ago = FormatTimeAgo(last.CompletedAt);
            status = last.Status == "error"
                ? $"Nexus Desktop — Error\nLast sync: {ago} (failed)"
                : $"Nexus Desktop — Idle\nLast sync: {ago}";
        } else {
            status = "Nexus Desktop — Idle\nLast sync: never";
        }

        _trayIcon.ToolTipText = status;
    }

    private static string FormatTimeAgo( DateTime time ) {
        var diff = DateTime.Now - time;
        if ( diff.TotalSeconds < 60 ) return $"{(int)diff.TotalSeconds}s ago";
        if ( diff.TotalMinutes < 60 ) return $"{(int)diff.TotalMinutes}m ago";
        return $"{(int)diff.TotalHours}h ago";
    }

    public void Dispose() {
        _syncEngine.SyncStarted   -= _onSyncStarted;
        _syncEngine.SyncCompleted -= _onSyncCompleted;
        _timer.Stop();
        _trayIcon.Dispose();
    }
}
