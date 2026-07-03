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
    private UpdateInfo? _pendingVelopackUpdate;
    private MainWindow? _mainWindow;

    public string? LastAiConfigVersion { get; private set; }

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
            _ = TriggerUpdateCheck();
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
        await TriggerUpdateCheck();
        StartUpdateTimer();
        await TriggerAiConfigCheck(isManualTrigger: false);
    }

    private void StartUpdateTimer() {
        if ( _updateTimer != null ) return;
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        _updateTimer.Tick += async (_, _) => {
            await TriggerUpdateCheck();
            await TriggerAiConfigCheck(isManualTrigger: false);
        };
        _updateTimer.Start();
    }

    public Task TriggerUpdateCheck() {
        return Task.Run(async () => {
            try {
                var manager = new UpdateManager(_config.NexusUrl + "/api/v1/app/releases");
                var updateInfo = await manager.CheckForUpdatesAsync();
                if ( updateInfo is null ) return;

                Dispatcher.Invoke(() => _pendingVelopackUpdate = updateInfo);

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
                    } else {
                        _pendingVelopackUpdate = null;
                    }
                });
            } catch {
                // Update failure must never crash the app
                Dispatcher.Invoke(() => _pendingVelopackUpdate = null);
            }
        });
    }

    /// <summary>Must be invoked on the UI dispatcher (calls ShowDialog).</summary>
    public async Task TriggerAiConfigCheck( bool isManualTrigger = false ) {
        if ( _aiConfigCheckInFlight ) return;
        if ( ! _config.IsLoggedIn ) return;
        if ( ! _config.AiSyncEnabled ) {
            if ( isManualTrigger ) {
                MessageBox.Show(
                    "AI Config Sync is disabled in Settings. Enable it via Settings to use this feature.",
                    "AI Config Sync",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            } else {
                _log.Write("TriggerAiConfigCheck: skipped — AI Config Sync is disabled in settings");
            }
            return;
        }
        if ( _pendingVelopackUpdate is not null ) {
            if ( isManualTrigger ) {
                MessageBox.Show(
                    "App update available — please restart to update, then re-check AI Config.",
                    "AI Config Sync",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            } else {
                _log.Write("TriggerAiConfigCheck: skipped — Velopack update pending");
            }
            return;
        }
        _aiConfigCheckInFlight = true;
        try {
            try {
                var result = await _api.GetAiConfigManifest();
                if ( ! result.Success ) {
                    if ( result.StatusCode == 404 ) {
                        if ( isManualTrigger ) {
                            MessageBox.Show(
                                "No AI Config snapshot available on the server.",
                                "AI Config Sync",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                        return;
                    }
                    if ( result.IsAuthError ) return;
                    _activity.Log("ai_config_check", $"manifest fetch failed: {result.Message}", "error");
                    if ( isManualTrigger ) {
                        MessageBox.Show(
                            result.Message ?? "Manifest fetch failed.",
                            "AI Config Sync",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    return;
                }
                var manifest = result.Data!;
                LastAiConfigVersion = manifest.Version;
                _mainWindow?.RefreshState();
                var currentFingerprint = File.Exists(AiConfigPaths.FingerprintPath)
                    ? File.ReadAllText(AiConfigPaths.FingerprintPath).Trim()
                    : "";
                var newFingerprint = ManifestFingerprint.Compute(manifest);
                if ( currentFingerprint == newFingerprint ) {
                    if ( isManualTrigger ) {
                        MessageBox.Show(
                            "AI Config is already up to date.",
                            "AI Config Sync",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    return;
                }
                var prompt = new AiConfigUpdatePromptWindow(manifest.Version);
                if ( prompt.ShowDialog() != true ) return;
                var apply = await _aiConfigApplyService!.ApplyAsync(manifest);
                _activity.Log("ai_config_apply", $"{apply.Status}: {apply.Message}", apply.Success ? "ok" : "error");
                _mainWindow?.RefreshState();
                if ( isManualTrigger ) {
                    if ( apply.Status == AiConfigApplyStatuses.Aborted ) {
                        MessageBox.Show(
                            apply.Message ?? "Apply aborted.",
                            "AI Config Sync",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    } else {
                        MessageBox.Show(
                            $"AI Config applied — version {apply.Version}, {manifest.Files.Count} files updated.",
                            "AI Config Sync",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }
            } catch ( Exception ex ) {
                _activity.Log("ai_config_check", $"unexpected error: {ex.Message}", "error");
                _log.Error("TriggerAiConfigCheck failed", ex);
                if ( isManualTrigger ) {
                    MessageBox.Show(
                        ex.Message,
                        "AI Config Sync",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
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
