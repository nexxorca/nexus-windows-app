using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

using Nexus.Core.Models;
using Nexus.Core.Services;
using Nexus.Sync.Services;

using Velopack;

namespace Nexus.App;

public partial class App : Application {
    private Mutex? _mutex;
    private TrayIconManager? _trayManager;
    private DispatcherTimer? _updateTimer;
    private AppConfig _config = null!;
    private LogService _log = null!;
    private ActivityLogService _activity = null!;
    private NexusApiClient _api = null!;
    private SyncEngine _syncEngine = null!;

    [STAThread]
    public static void Main( string[] args ) {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup( StartupEventArgs e ) {
        _mutex = new Mutex(true, "NexusDesktopApp", out bool isNew);
        if ( ! isNew ) {
            MessageBox.Show("Nexus Desktop is already running.", "Nexus Desktop",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Load config
        _config = AppConfig.Load();

        // Wire services
        _log = new LogService();
        _activity = new ActivityLogService();
        _api = new NexusApiClient(_log);

        _activity.Log("app_start", "Nexus Desktop started");

        // Wire sync engine
        var stateManager = new StateManager(_log);
        var scanner = new TranscriptScanner(_log);
        var parser = new TranscriptParser(_log);
        var mapper = new SubagentMapper(_log);
        _syncEngine = new SyncEngine(_config, _api, stateManager, scanner, parser, mapper, _log, _activity);
        _syncEngine.OnAuthFailed = () => Dispatcher.Invoke(ShowLoginWindow);

        // Init tray icon
        _trayManager = new TrayIconManager(_config, _syncEngine, _api, _activity, _log, this);

        if ( ! _config.IsLoggedIn ) {
            ShowLoginWindow();
        } else {
            _api.Configure(_config.NexusUrl, _config.DecryptedToken);
            _trayManager.StartSync();
            TriggerUpdateCheck();
            StartUpdateTimer();
        }
    }

    public void ShowLoginWindow() {
        var loginWindow = new Views.LoginWindow(_config, _api, _log, _activity);
        loginWindow.LoginSucceeded += OnLoginSucceeded;
        loginWindow.Show();
    }

    private void OnLoginSucceeded( object? sender, EventArgs e ) {
        _api.Configure(_config.NexusUrl, _config.DecryptedToken);
        _trayManager?.UpdateUserName(_config.UserName ?? "");
        _trayManager?.StartSync();
        TriggerUpdateCheck();
        StartUpdateTimer();
    }

    private void StartUpdateTimer() {
        if ( _updateTimer != null ) return;
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        _updateTimer.Tick += (_, _) => TriggerUpdateCheck();
        _updateTimer.Start();
    }

    public void TriggerUpdateCheck() {
        _ = Task.Run(async () => {
            try {
                var currentVersion = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetName().Version?.ToString(3) ?? "1.0.0";

                var update = await _api.CheckForUpdateAsync(currentVersion);
                if ( update is null ) return;

                var manager = new UpdateManager(update.Value.DownloadUrl);
                var updateInfo = await manager.CheckForUpdatesAsync();
                if ( updateInfo is null ) return;

                await manager.DownloadUpdatesAsync(updateInfo);

                Dispatcher.Invoke(() => {
                    var result = MessageBox.Show(
                        $"Version {update.Value.Version} is available. Restart now to update?",
                        "Update Available",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information
                    );
                    if ( result == MessageBoxResult.Yes ) {
                        manager.ApplyUpdatesAndRestart(updateInfo);
                    }
                });
            } catch {
                // Update failure must never crash the app
            }
        });
    }

    public async void Logout() {
        _trayManager?.StopSync();
        _activity.Log("logout", $"User {_config.UserName} logged out");
        await _api.RevokeToken();
        _config.ClearLoginData();
        _trayManager?.UpdateUserName("");
        ShowLoginWindow();
    }

    protected override void OnExit( ExitEventArgs e ) {
        _activity.Log("app_stop", "Nexus Desktop stopped");
        _activity.Flush();
        _updateTimer?.Stop();
        _trayManager?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

