using System.ComponentModel;
using System.Reflection;
using System.Windows;

using Nexus.Core.Models;
using Nexus.Core.Services;
using Nexus.Sync.Models;
using Nexus.Sync.Services;

namespace Nexus.App;

public partial class MainWindow : Window {
    private readonly AppConfig _config;
    private readonly SyncEngine _syncEngine;
    private readonly ActivityLogService _activity;
    private readonly App _app;
    private bool _allowClose;
    private Action _onSyncStarted;
    private Action<SyncResult> _onSyncCompleted;

    public MainWindow( AppConfig config, SyncEngine syncEngine, ActivityLogService activity, App app ) {
        _config = config;
        _syncEngine = syncEngine;
        _activity = activity;
        _app = app;
        InitializeComponent();

        var ui = Application.Current.Dispatcher;
        _onSyncStarted   = () => ui.Invoke(() => { if ( ! IsVisible ) return; RefreshState(); });
        _onSyncCompleted = _  => ui.Invoke(() => { if ( ! IsVisible ) return; RefreshState(); });
        _syncEngine.SyncStarted   += _onSyncStarted;
        _syncEngine.SyncCompleted += _onSyncCompleted;
    }

    public void AllowClose() => _allowClose = true;

    public void RefreshState() {
        lblUserName.Text        = string.IsNullOrEmpty(_config.UserName) ? "—" : _config.UserName;
        lblVersion.Text         = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");
        lblAiConfigVersion.Text = _app.LastAiConfigVersion ?? "—";

        if ( _syncEngine.IsRunning ) {
            lblStatus.Text   = "Syncing...";
            lblLastSync.Text = _syncEngine.LastSync is { } prev ? FormatTimeAgo(prev.CompletedAt) : "never";
        } else if ( _syncEngine.LastSync is { } last ) {
            lblStatus.Text   = last.Status == "error" ? "Error" : "Idle";
            lblLastSync.Text = FormatTimeAgo(last.CompletedAt);
        } else {
            lblStatus.Text   = "Idle";
            lblLastSync.Text = "never";
        }
    }

    private static string FormatTimeAgo( DateTime time ) {
        var diff = DateTime.Now - time;
        if ( diff.TotalSeconds < 60 ) return $"{(int)diff.TotalSeconds}s ago";
        if ( diff.TotalMinutes < 60 ) return $"{(int)diff.TotalMinutes}m ago";
        return $"{(int)diff.TotalHours}h ago";
    }

    private async void btnSyncNow_Click( object sender, RoutedEventArgs e )    => await _app.TriggerSync();
    private void btnActivityLog_Click( object sender, RoutedEventArgs e )      => _app.ShowActivityLogWindow();
    private void btnSettings_Click( object sender, RoutedEventArgs e )         => _app.ShowSettingsWindow();
    private void btnCheckUpdates_Click( object sender, RoutedEventArgs e )     { _ = _app.TriggerUpdateCheck(); }
    private void btnCheckAiConfig_Click( object sender, RoutedEventArgs e )    { _ = _app.TriggerAiConfigCheck(isManualTrigger: true); }
    private void btnLogout_Click( object sender, RoutedEventArgs e )           { _ = _app.Logout(); }

    protected override void OnClosing( CancelEventArgs e ) {
        if ( ! _allowClose ) {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed( EventArgs e ) {
        _syncEngine.SyncStarted   -= _onSyncStarted;
        _syncEngine.SyncCompleted -= _onSyncCompleted;
        base.OnClosed(e);
    }
}
