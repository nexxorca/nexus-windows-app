using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

using Nexus.Core.Models;
using Nexus.Core.Services;
using Nexus.Sync.Models;
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
    private AiConfigApplyService? _aiConfigApplyService;
    private bool _aiConfigCheckInFlight;
    private MainWindow? _mainWindow;

    [STAThread]
    public static void Main( string[] args ) {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup( StartupEventArgs e ) {
        base.OnStartup(e);

        CleanupLegacyAiConfigArtifacts();

        _mutex = new Mutex(true, "NexusDesktopApp", out bool isNew);
        if ( ! isNew ) {
            MessageBox.Show("Nexus Desktop is already running.", "Nexus Desktop",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

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
        _aiConfigApplyService = new AiConfigApplyService(_api, _activity, _log);

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

    private async void OnLoginSucceeded( object? sender, EventArgs e ) {
        _api.Configure(_config.NexusUrl, _config.DecryptedToken);
        _trayManager?.UpdateUserName(_config.UserName ?? "");
        _trayManager?.StartSync();
        TriggerUpdateCheck();
        StartUpdateTimer();
        await TriggerAiConfigCheck();
    }

    private void StartUpdateTimer() {
        if ( _updateTimer != null ) return;
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        _updateTimer.Tick += (_, _) => {
            TriggerUpdateCheck();
            _ = TriggerAiConfigCheck();  // fire-and-forget; UI thread (Tick fires on dispatcher)
        };
        _updateTimer.Start();
    }

    public void TriggerUpdateCheck() {
        _ = Task.Run(async () => {
            try {
                var manager = new UpdateManager(_config.NexusUrl + "/api/v1/app/releases");
                var updateInfo = await manager.CheckForUpdatesAsync();
                if ( updateInfo is null ) return;

                await manager.DownloadUpdatesAsync(updateInfo);

                Dispatcher.Invoke(() => {
                    var newVersion = updateInfo.TargetFullRelease.Version.ToString();
                    var result = MessageBox.Show(
                        $"Version {newVersion} is available. Restart now to update?",
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

    /// <summary>Must be invoked on the UI dispatcher (calls ShowDialog).</summary>
    public async Task TriggerAiConfigCheck() {
        if ( _aiConfigCheckInFlight ) return;
        if ( ! _config.IsLoggedIn ) return;
        if ( ! ClaudeCodeInstallProbe.IsInstalled() ) {
            _activity.Log("ai_config_check", "Claude Code not installed — sync skipped", "warning");
            return;
        }
        _aiConfigCheckInFlight = true;
        try {
            try {
                var result = await _api.GetAiConfigManifest();
                if ( ! result.Success ) {
                    if ( result.StatusCode == 404 ) return;
                    _activity.Log("ai_config_check", $"manifest fetch failed: {result.Message}", "error");
                    return;
                }
                var manifest = result.Data!;
                var currentFingerprint = File.Exists(AiConfigPaths.FingerprintPath)
                    ? File.ReadAllText(AiConfigPaths.FingerprintPath).Trim()
                    : "";
                var newFingerprint = ManifestFingerprint.Compute(manifest);
                if ( currentFingerprint == newFingerprint ) return;
                if ( ClaudeCodeInstallProbe.IsRunning() ) {
                    _activity.Log("ai_config_check", "Claude Code is running — apply deferred. Close Claude Code and click 'Check AI Config' again.", "warning");
                    _mainWindow?.RefreshState();
                    return;
                }
                var prompt = new AiConfigUpdatePromptWindow(manifest.Version);
                if ( prompt.ShowDialog() != true ) return;
                var apply = await _aiConfigApplyService!.ApplyAsync(manifest);
                _activity.Log("ai_config_apply", $"{apply.Status}: {apply.Message}", apply.Success ? "ok" : "error");
                _mainWindow?.RefreshState();
            } catch ( Exception ex ) {
                _activity.Log("ai_config_check", $"unexpected error: {ex.Message}", "error");
                _log.Error("TriggerAiConfigCheck failed", ex);
            }
        } finally {
            _aiConfigCheckInFlight = false;
        }
    }

    public void ShowMainWindow() {
        if ( ! _config.IsLoggedIn ) return;
        _mainWindow ??= new MainWindow(_config, _syncEngine, _activity, this);
        _mainWindow.RefreshState();
        _mainWindow.Show();
        if ( _mainWindow.WindowState == WindowState.Minimized ) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public async Task TriggerSync() {
        if ( ! _config.IsLoggedIn ) return;
        await _syncEngine.RunSync();
    }

    public void ShowActivityLogWindow() {
        var window = new Views.ActivityLogWindow(_activity);
        window.Show();
    }

    public void ShowSettingsWindow() {
        var window = new Views.SettingsWindow(_config, _activity);
        window.Closed += ( s, e ) => {
            _trayManager?.StopSync();
            if ( _config.IsLoggedIn ) _trayManager?.StartSync();
        };
        window.Show();
    }

    public void PrepareForShutdown() {
        _mainWindow?.AllowClose();
        _mainWindow?.Close();
    }

    public async Task Logout() {
        _trayManager?.StopSync();
        _mainWindow?.Hide();
        _mainWindow?.RefreshState();
        _activity.Log("logout", $"User {_config.UserName} logged out");
        await _api.RevokeToken();
        _config.ClearLoginData();
        _trayManager?.UpdateUserName("");
        ShowLoginWindow();
    }

    // One-shot legacy cleanup for artifacts left by 1.2.0/1.2.1.
    // Runs before any sync or AI-config trigger can fire. Never throws — must not block startup.
    private static void CleanupLegacyAiConfigArtifacts() {
        // Delete the legacy backup tree (~/.claude/backups/) created by the old surgical-apply pipeline.
        // Idempotent — safe to run on every startup once the directory is gone.
        if ( Directory.Exists(AiConfigPaths.BackupsRoot) ) {
            try {
                Directory.Delete(AiConfigPaths.BackupsRoot, recursive: true);
            } catch {
                // Best-effort — startup must not be blocked
            }
        }

        // Delete the legacy ai-config-version marker file written by the pre-fingerprint pipeline.
        // Path is hardcoded here because AiConfigPaths.VersionMarkerPath was removed in 1.2.2.
        var versionMarker = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nexus", "ai-config-version");
        if ( File.Exists(versionMarker) ) {
            try {
                File.Delete(versionMarker);
            } catch {
                // Best-effort
            }
        }
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
