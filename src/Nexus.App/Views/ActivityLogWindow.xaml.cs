using System.Windows;
using System.Windows.Controls;

using Nexus.Core.Services;

namespace Nexus.App.Views;

public partial class ActivityLogWindow : Window {
    private readonly ActivityLogService _activity;

    public ActivityLogWindow( ActivityLogService activity ) {
        InitializeComponent();
        _activity = activity;
        LoadEvents();
    }

    private void LoadEvents() {
        var filter = GetSelectedFilter();
        var typeFilter = filter switch {
            "sync" => "sync_start,sync_complete",
            "uploads" => "file_upload,file_skip,subagent_found",
            "errors" => "api_error",
            _ => null
        };

        // For multi-type filters, get all and filter manually
        var events = _activity.GetRecent(200);

        if ( typeFilter != null ) {
            var types = typeFilter.Split(',');
            events = events.Where(e => types.Contains(e.Type)).ToList();
        }

        lstEvents.ItemsSource = events;
    }

    private string GetSelectedFilter() {
        if ( cmbFilter.SelectedItem is ComboBoxItem item && item.Tag is string tag ) {
            return tag;
        }
        return "all";
    }

    private void CmbFilter_SelectionChanged( object sender, SelectionChangedEventArgs e ) {
        if ( lstEvents != null ) LoadEvents();
    }

    private void BtnRefresh_Click( object sender, RoutedEventArgs e ) {
        LoadEvents();
    }

    private void BtnClose_Click( object sender, RoutedEventArgs e ) {
        Close();
    }
}
