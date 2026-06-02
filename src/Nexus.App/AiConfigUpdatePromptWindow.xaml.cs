using System.Windows;

namespace Nexus.App;

public partial class AiConfigUpdatePromptWindow : Window {
    public AiConfigUpdatePromptWindow( string newVersion ) {
        InitializeComponent();
        lblBody.Text = $"New AI config available: v{newVersion}. Close Claude Code sessions before applying — files in ~/.claude/ will be replaced.";
        Activate();
    }

    private void btnApplyNow_Click( object sender, RoutedEventArgs e ) {
        DialogResult = true;
        Close();
    }

    private void btnLater_Click( object sender, RoutedEventArgs e ) {
        DialogResult = false;
        Close();
    }
}
