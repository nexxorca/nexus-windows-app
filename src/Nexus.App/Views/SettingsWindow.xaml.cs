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
        txtLookback.Text = config.LookbackMinutes.ToString();
    }

    private void BtnSave_Click( object sender, RoutedEventArgs e ) {
        if ( int.TryParse(txtInterval.Text, out var interval) && interval > 0 ) {
            _config.SyncIntervalSeconds = interval;
        }

        if ( int.TryParse(txtLookback.Text, out var lookback) && lookback > 0 ) {
            _config.LookbackMinutes = lookback;
        }

        _config.Save();
        _activity.Log("config_change", $"Settings updated: interval={_config.SyncIntervalSeconds}s, lookback={_config.LookbackMinutes}m");
        Close();
    }

    private void BtnCancel_Click( object sender, RoutedEventArgs e ) {
        Close();
    }
}
