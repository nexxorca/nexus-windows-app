using System.Text.Json;
using System.Windows;

using Nexus.Core.Models;
using Nexus.Core.Services;

namespace Nexus.App.Views;

public partial class LoginWindow : Window {
    private readonly AppConfig _config;
    private readonly NexusApiClient _api;
    private readonly LogService _log;
    private readonly ActivityLogService _activity;

    public event EventHandler? LoginSucceeded;

    public LoginWindow( AppConfig config, NexusApiClient api, LogService log, ActivityLogService activity ) {
        InitializeComponent();
        _config = config;
        _api = api;
        _log = log;
        _activity = activity;

        // Pre-fill URL if previously set
        if ( ! string.IsNullOrEmpty(_config.NexusUrl) ) {
            txtUrl.Text = _config.NexusUrl;
        }
    }

    private async void BtnLogin_Click( object sender, RoutedEventArgs e ) {
        lblError.Visibility = Visibility.Collapsed;
        btnLogin.IsEnabled = false;
        btnLogin.Content = "Logging in...";

        var url = txtUrl.Text.Trim();
        var email = txtEmail.Text.Trim();
        var password = txtPassword.Password;

        if ( string.IsNullOrEmpty(url) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password) ) {
            ShowError("Please fill in all fields.");
            return;
        }

        var result = await _api.Login(url, email, password);

        if ( ! result.Success ) {
            if ( result.IsRateLimited ) {
                ShowError(result.Message ?? "Too many login attempts. Please wait before trying again.");
                return;
            }

            var msg = result.IsAuthError
                ? "Invalid email or password."
                : result.Message ?? "Login failed. Check your connection.";
            ShowError(msg);
            return;
        }

        // Parse response to get token and user info
        try {
            using var doc = JsonDocument.Parse(result.Message ?? "{}");
            var root = doc.RootElement;
            var token = root.GetProperty("token").GetString() ?? "";
            var userHashId = root.GetProperty("user").GetProperty("hash_id").GetString() ?? "";
            var userName = root.GetProperty("user").GetProperty("first_name").GetString() ?? "";

            _config.NexusUrl = url;
            _config.SetLoginData(token, userHashId, userName);
            _activity.Log("login", $"User {userName} logged in");

            LoginSucceeded?.Invoke(this, EventArgs.Empty);
            Close();
        } catch ( Exception ex ) {
            _log.Error("Failed to parse login response", ex);
            ShowError("Unexpected server response.");
        }
    }

    private void ShowError( string message ) {
        lblError.Text = message;
        lblError.Visibility = Visibility.Visible;
        btnLogin.IsEnabled = true;
        btnLogin.Content = "Login";
    }
}
