using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Hardcodet.Wpf.TaskbarNotification;

using Nexus.Core.Models;
using Nexus.Core.Services;
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
    private DateTime? _lastSyncTime;
    private bool _isSyncing;
    private bool _lastSyncFailed;
    private string _userName = "";

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

        _timer = new DispatcherTimer {
            Interval = TimeSpan.FromSeconds(config.SyncIntervalSeconds)
        };
        _timer.Tick += async ( s, e ) => await RunSync();

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

        var syncNow = new MenuItem { Header = "Sync Now" };
        syncNow.Click += async ( s, e ) => await RunSync();
        menu.Items.Add(syncNow);

        var activityLog = new MenuItem { Header = "Activity Log" };
        activityLog.Click += ( s, e ) => ShowActivityLog();
        menu.Items.Add(activityLog);

        var settings = new MenuItem { Header = "Settings" };
        settings.Click += ( s, e ) => ShowSettings();
        menu.Items.Add(settings);

        menu.Items.Add(new Separator());

        if ( _config.IsLoggedIn ) {
            var logout = new MenuItem { Header = "Logout" };
            logout.Click += ( s, e ) => _app.Logout();
            menu.Items.Add(logout);
        }

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += ( s, e ) => {
            _app.Shutdown();
        };
        menu.Items.Add(exit);

        return menu;
    }

    private async Task RunSync() {
        if ( _isSyncing ) return;
        if ( ! _config.IsLoggedIn ) return;

        _isSyncing = true;
        _timer.Stop();

        try {
            await _syncEngine.RunSync();
            _lastSyncTime = DateTime.Now;
            _lastSyncFailed = _syncEngine.LastSyncHadErrors;
        } catch ( Exception ex ) {
            _log.Error("Sync failed", ex);
            _lastSyncFailed = true;
        } finally {
            _isSyncing = false;
            UpdateTooltip();
            _timer.Start();
        }
    }

    private void UpdateTooltip() {
        string status;
        if ( ! _config.IsLoggedIn ) {
            status = "Nexus Desktop — Not logged in";
        } else if ( _lastSyncFailed ) {
            var ago = _lastSyncTime.HasValue ? FormatTimeAgo(_lastSyncTime.Value) : "never";
            status = $"Nexus Desktop — Error\nLast sync: {ago} (failed)";
        } else if ( _lastSyncTime.HasValue ) {
            var ago = FormatTimeAgo(_lastSyncTime.Value);
            status = $"Nexus Desktop — Idle\nLast sync: {ago}";
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

    private void ShowActivityLog() {
        var window = new Views.ActivityLogWindow(_activity);
        window.Show();
    }

    private void ShowSettings() {
        var window = new Views.SettingsWindow(_config, _activity);
        window.Closed += ( s, e ) => {
            // Restart timer with potentially new interval
            _timer.Stop();
            _timer.Interval = TimeSpan.FromSeconds(_config.SyncIntervalSeconds);
            if ( _config.IsLoggedIn ) _timer.Start();
        };
        window.Show();
    }

    public void Dispose() {
        _timer.Stop();
        _trayIcon.Dispose();
    }
}
