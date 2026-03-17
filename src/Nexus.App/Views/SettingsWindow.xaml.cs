using System.Windows;

using Nexus.Core.Models;
using Nexus.Core.Services;

namespace Nexus.App.Views;

public partial class SettingsWindow : Window {
    private readonly AppConfig _config;
    private readonly ActivityLogService _activity;

    public SettingsWindow( AppConfig config, ActivityLogService activity ) {
        InitializeComponent();
        _config = config;
        _activity = activity;

        txtInterval.Text = config.SyncIntervalSeconds.ToString();
        chkLaunchOnStartup.IsChecked = config.LaunchOnStartup;
        lblVersion.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");
    }

    private void BtnSave_Click( object sender, RoutedEventArgs e ) {
        if ( int.TryParse(txtInterval.Text, out var interval) && interval > 0 ) {
            _config.SyncIntervalSeconds = interval;
        }

        var launchOnStartup = chkLaunchOnStartup.IsChecked == true;
        StartupManager.SetLaunchOnStartup(launchOnStartup);
        _config.LaunchOnStartup = launchOnStartup;

        _config.Save();
        _activity.Log("config_change", $"Settings updated: interval={_config.SyncIntervalSeconds}s, launchOnStartup={launchOnStartup}");
        Close();
    }

    private void BtnCancel_Click( object sender, RoutedEventArgs e ) {
        Close();
    }
}
