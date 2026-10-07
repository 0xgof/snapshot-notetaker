using System.IO;
using System.Windows;
using System.Windows.Threading;
using SnapshotNotetaker.Capture;
using SnapshotNotetaker.IO;
using SnapshotNotetaker.Settings;
using SnapshotNotetaker.Themes;
using SnapshotNotetaker.Views;
using SnapshotNotetaker.Support;

namespace SnapshotNotetaker;

public partial class App : Application
{
    private const string MutexName = @"Local\SnapshotNotetaker.Instance";
    private const string ActivateEventName = @"Local\SnapshotNotetaker.Activate";
    private static readonly string ForwardFile = Path.Combine(AppSettings.AppDataFolder, "forward.txt");

    private readonly Dictionary<HotkeyAction, string> _hotkeyErrors = new();
    private MainWindow? _main;
    private HotkeyService? _hotkeys;
    private TrayIcon? _tray;
    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private DispatcherTimer? _settingsTimer;
    private SettingsWindow? _settingsWindow;
    private SupportWindow? _supportWindow;
    private bool _devMode;

    public AppSettings Settings { get; private set; } = new();
    public SnapshotLibrary Library { get; private set; } = null!;
    public CaptureService Capture { get; } = new();
    public bool IsExiting { get; private set; }

    /// <summary>Library folder of the snapshot open in the editor (for support bundles).</summary>
    public string? CurrentSnapshotFolder => _main?.CurrentSnapshotFolder;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashHandler.Install();
        DispatcherUnhandledException += OnUnhandledException;
        var startup = System.Diagnostics.Stopwatch.StartNew();
        double runtimeMs = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        var phases = new List<string> { $"runtime {runtimeMs:0}" };
        long lastMark = 0;
        void Mark(string phase)
        {
            phases.Add($"{phase} {startup.ElapsedMilliseconds - lastMark}");
            lastMark = startup.ElapsedMilliseconds;
        }
        var args = e.Args;

        if (DevTools.TryRun(this, args)) return;

        _mutex = new Mutex(true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            ForwardToRunningInstance(args);
            Shutdown();
            return;
        }
        ListenForOtherInstances();

        Settings = AppSettings.Load();
        Mark("settings");
        Log.MinimumLevel = Settings.DetailedLogging ? LogLevel.Debug : LogLevel.Info;
        Log.Info("app", $"Snapshot Notetaker {BuildInfo.Version} starting (session {Log.SessionId}){(args.Length > 0 ? $" with {args.Length} argument(s)" : "")}.");
        ErrorReporting.Apply(Settings.SendCrashReports == true, Settings.InstallId);
        Mark("reporting");
        ThemeManager.Apply(Settings.Theme);
        Mark("theme");
        Library = new SnapshotLibrary(Settings.ResolvedLibraryFolder);

        _main = new MainWindow(this);
        MainWindow = _main;
        Mark("window");
        bool startInTray = args.Contains("--tray", StringComparer.OrdinalIgnoreCase) || Settings.StartMinimizedToTray;
        if (!startInTray) _main.Show();
        Mark("show");

        // Scan the library while the first frame renders, then set up the tray icon and shortcuts
        // (≈0.3 s of WinForms/Win32 work the user doesn't need to wait for).
        var libraryTiming = Log.Time("library", "Library load");
        var libraryLoad = Library.LoadAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Mark("paint");

        _tray = new TrayIcon(this);
        Mark("tray");
        _hotkeys = new HotkeyService();
        ApplyHotkeys();
        Mark("hotkeys");

        try
        {
            await libraryLoad;
            libraryTiming.Detail = $"{Library.Entries.Count} snapshots";
        }
        catch (Exception ex)
        {
            libraryTiming.Outcome = "failed after";
            Log.Error("library", "Could not load the snapshot library.", ex);
        }
        finally
        {
            libraryTiming.Dispose();
        }

        Mark("library");
        var files = args.Where(a => !a.StartsWith("--") && File.Exists(a)).ToList();
        if (files.Count > 0) await _main.ImportFilesAsync(files);
        else if (Settings.OpenLastSnapshotOnStart) await _main.OpenLastSnapshotAsync();
        Mark("open");

        Log.Info("app", $"Ready in {startup.ElapsedMilliseconds + runtimeMs:0} ms since launch ({string.Join(", ", phases)} ms).");
        Log.Info("app", "System:" + Environment.NewLine + SystemInfo.Describe());
        CheckSupportNotices();
    }

    /// <summary>After startup: tell the user if the last run crashed, or ask once about crash reports.</summary>
    private void CheckSupportNotices()
    {
        if (_main == null) return;
        var lastCheck = Settings.LastCrashCheckUtc;
        Settings.LastCrashCheckUtc = DateTime.UtcNow;
        SaveSettingsSoon();

        if (lastCheck != default && CrashHandler.FatalSince(lastCheck) is { } report)
        {
            Log.Info("support", "The previous session ended with a crash; offering a support bundle.");
            _main.ShowInfoBar("Snapshot Notetaker closed unexpectedly last time. A support bundle helps get it fixed.",
                "Create support bundle…", () => _ = SupportActions.CreateBundleAsync(_main),
                "Details…", () => ProblemWindow.ShowPreviousCrash(_main, report));
        }
        else if (ErrorReporting.IsAvailable && Settings.SendCrashReports == null)
        {
            _main.ShowInfoBar("Help fix problems faster: send crash reports automatically? They contain technical details only, never your screenshots or notes.",
                "Send reports", () => SetCrashReporting(true),
                "No thanks", () => SetCrashReporting(false));
        }
    }

    public void SetCrashReporting(bool enabled)
    {
        Settings.SendCrashReports = enabled;
        ErrorReporting.Apply(enabled, Settings.InstallId);
        Log.Info("support", $"Crash reports {(enabled ? "enabled" : "disabled")} by the user.");
        SaveSettingsSoon();
    }

    public void OpenSupport()
    {
        if (_supportWindow != null)
        {
            _supportWindow.Activate();
            return;
        }
        _supportWindow = new SupportWindow(this);
        if (_main is { IsVisible: true }) _supportWindow.Owner = _main;
        try
        {
            _supportWindow.ShowDialog();
        }
        finally
        {
            _supportWindow = null;
        }
    }

    /// <summary>Starts the app in a sandbox (no tray, no hotkeys, no settings writes). Used by DevTools.</summary>
    internal MainWindow StartSandbox(AppSettings settings)
    {
        _devMode = true;
        Settings = settings;
        ThemeManager.Apply(settings.Theme);
        Library = new SnapshotLibrary(settings.ResolvedLibraryFolder);
        _main = new MainWindow(this);
        MainWindow = _main;
        return _main;
    }

    // ------------------------------------------------------------------ actions

    public async void RunAction(HotkeyAction action)
    {
        if (action == HotkeyAction.ShowApp)
        {
            ShowMainWindow();
            return;
        }
        if (Capture.IsBusy || _main == null) return;
        using var timing = Log.Time("capture", action.ToString());
        try
        {
            CaptureResult? result = action switch
            {
                HotkeyAction.CaptureRegion => await Capture.CaptureRegionAsync(),
                HotkeyAction.CaptureWindow => await Capture.CaptureWindowAsync(),
                HotkeyAction.CaptureScreen => await Capture.CaptureScreenAsync(),
                HotkeyAction.CaptureAllScreens => await Capture.CaptureAllScreensAsync(),
                _ => null,
            };
            if (result == null) timing.Outcome = "cancelled after";
            else timing.Detail = $"{result.Image.PixelWidth}×{result.Image.PixelHeight} at {result.DpiScale * 100:0}%";
            Deliver(result);
        }
        catch (Exception ex)
        {
            timing.Outcome = "failed after";
            ReportCaptureError(ex);
        }
    }

    public async void CaptureMonitor(string deviceName)
    {
        try
        {
            Deliver(await Capture.CaptureScreenAsync(deviceName));
        }
        catch (Exception ex)
        {
            ReportCaptureError(ex);
        }
    }

    public void CaptureSpecificWindow(WindowInfo window)
    {
        try
        {
            var result = Capture.CaptureSpecificWindow(window);
            if (result == null) MessageBox.Show("That window could not be captured (it may have closed).", "Snapshot Notetaker");
            Deliver(result);
        }
        catch (Exception ex)
        {
            ReportCaptureError(ex);
        }
    }

    private void Deliver(CaptureResult? result)
    {
        if (result == null || _main == null) return;
        ShowMainWindow();
        _main.AddCapture(result);
    }

    private void ReportCaptureError(Exception ex)
    {
        Log.Error("capture", "Capture failed.", ex);
        ErrorReporting.Capture(ex);
        ProblemWindow.ShowError(_main, "The capture failed.", ex);
    }

    public void ShowMainWindow()
    {
        if (_main == null) return;
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true; // reliably bring to front when triggered from a hotkey or the tray
        _main.Topmost = false;
    }

    public void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _hotkeys?.UnregisterAll(); // so pressing a shortcut while recording doesn't trigger a capture
        _settingsWindow = new SettingsWindow(this);
        if (_main is { IsVisible: true }) _settingsWindow.Owner = _main;
        try
        {
            _settingsWindow.ShowDialog();
        }
        finally
        {
            _settingsWindow = null;
            ApplyHotkeys();
            _main?.OnSettingsChanged();
        }
    }

    public async Task ChangeLibraryFolderAsync(string? folder, bool moveExisting)
    {
        _main?.SaveNow();
        string target = string.IsNullOrWhiteSpace(folder) ? AppSettings.DefaultLibraryFolder : folder;
        _main?.ResetForLibraryChange();
        await Library.ChangeRootAsync(target, moveExisting);
        Settings.LibraryFolder = folder;
        if (_main != null) await _main.OpenLastSnapshotAsync();
    }

    // ------------------------------------------------------------------ hotkeys

    public IReadOnlyDictionary<HotkeyAction, string> HotkeyErrors => _hotkeyErrors;

    public void ApplyHotkeys()
    {
        if (_hotkeys == null) return;
        _hotkeys.UnregisterAll();
        _hotkeyErrors.Clear();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var hotkey = Settings.GetHotkey(action);
            var a = action;
            string? error = _hotkeys.Register(hotkey, () => RunAction(a));
            if (error != null) _hotkeyErrors[action] = $"{hotkey} — {error}";
        }
        string summary = string.Join(", ", Enum.GetValues<HotkeyAction>().Where(a => !Settings.GetHotkey(a).IsEmpty)
            .Select(a => $"{a}={Settings.GetHotkey(a)}{(_hotkeyErrors.ContainsKey(a) ? " FAILED" : "")}"));
        if (_hotkeyErrors.Count > 0) Log.Warn("hotkeys", $"Shortcuts: {summary}. Failures: {string.Join("; ", _hotkeyErrors.Values)}");
        else Log.Info("hotkeys", $"Shortcuts: {(summary.Length > 0 ? summary : "none")}.");
        _tray?.Refresh();
        if (_hotkeyErrors.Count > 0)
            _tray?.ShowBalloon("Some shortcuts are unavailable", string.Join("\n", _hotkeyErrors.Values) + "\nChange them in Settings.");
    }

    public string? TestHotkey(Hotkey hotkey) => _hotkeys?.Test(hotkey);

    // ------------------------------------------------------------------ lifetime

    public void SaveSettingsSoon()
    {
        if (_devMode) return;
        if (_settingsTimer == null)
        {
            _settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _settingsTimer.Tick += (_, _) =>
            {
                _settingsTimer.Stop();
                Settings.Save();
            };
        }
        _settingsTimer.Stop();
        _settingsTimer.Start();
    }

    public void NotifyRunningInTray()
    {
        if (Settings.TrayHintShown) return;
        Settings.TrayHintShown = true;
        SaveSettingsSoon();
        var region = Settings.GetHotkey(HotkeyAction.CaptureRegion);
        _tray?.ShowBalloon("Snapshot Notetaker is still running",
            region.IsEmpty ? "Use the tray icon to capture." : $"Press {region} to capture. Right-click the tray icon to exit.");
    }

    public void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        Log.Info("app", "Exiting.");
        try
        {
            _main?.SaveNow();
            Library?.Flush();
            if (!_devMode) Settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("app", "Error while exiting.", ex);
        }
        _hotkeys?.Dispose();
        _hotkeys = null;
        _tray?.Dispose();
        _tray = null;
        ErrorReporting.Flush();
        Log.Flush(TimeSpan.FromSeconds(2));
        if (_main is { IsLoaded: true }) _main.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _activateEvent?.Set(); // releases the listener thread
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // The UI thread survives these: log, keep a report, tell the user how to get help, carry on.
        e.Handled = true;
        Log.Error("crash", "Unhandled exception on the UI thread.", e.Exception);
        CrashHandler.WriteReport(e.Exception, fatal: false);
        ErrorReporting.Capture(e.Exception);
        ProblemWindow.ShowError(_main, "Something went wrong", e.Exception);
    }

    // ------------------------------------------------------------------ single instance

    private static void ForwardToRunningInstance(string[] args)
    {
        try
        {
            var files = args.Where(a => !a.StartsWith("--") && File.Exists(a)).Select(Path.GetFullPath).ToList();
            Directory.CreateDirectory(AppSettings.AppDataFolder);
            File.WriteAllLines(ForwardFile, files);
            using var signal = EventWaitHandle.OpenExisting(ActivateEventName);
            signal.Set();
        }
        catch (Exception ex)
        {
            Log.Warn("app", "Could not reach the running instance.", ex);
        }
    }

    private void ListenForOtherInstances()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(() =>
        {
            while (_activateEvent.WaitOne())
            {
                if (IsExiting) return;
                Dispatcher.BeginInvoke(OnActivatedByOtherInstance);
            }
        })
        { IsBackground = true, Name = "SingleInstanceListener" };
        thread.Start();
    }

    private async void OnActivatedByOtherInstance()
    {
        if (IsExiting || _main == null) return;
        ShowMainWindow();
        try
        {
            if (!File.Exists(ForwardFile)) return;
            var files = File.ReadAllLines(ForwardFile).Where(File.Exists).ToList();
            File.Delete(ForwardFile);
            if (files.Count > 0) await _main.ImportFilesAsync(files);
        }
        catch (Exception ex)
        {
            Log.Warn("app", "Could not read forwarded files.", ex);
        }
    }
}
