using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SnapshotNotetaker.Capture;
using SnapshotNotetaker.Settings;
using SnapshotNotetaker.Themes;
using SnapshotNotetaker.Support;

namespace SnapshotNotetaker.Views;

public partial class SettingsWindow : Window
{
    private static readonly (HotkeyAction Action, string Label)[] Actions =
    {
        (HotkeyAction.CaptureRegion, "Capture region"),
        (HotkeyAction.CaptureWindow, "Capture window"),
        (HotkeyAction.CaptureScreen, "Capture display under mouse"),
        (HotkeyAction.CaptureAllScreens, "Capture all displays"),
        (HotkeyAction.ShowApp, "Show Snapshot Notetaker"),
    };

    private readonly App _app;
    private readonly string _originalTheme;
    private readonly Dictionary<HotkeyAction, (HotkeyBox Box, TextBlock Status)> _rows = new();
    private string? _libraryFolder;

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var settings = app.Settings;
        _originalTheme = settings.Theme;
        _libraryFolder = settings.LibraryFolder;

        BuildHotkeyRows(settings);

        foreach (var name in ThemeManager.Names)
            ThemeCombo.Items.Add(new ComboBoxItem { Content = name == ThemeManager.SystemThemeName ? "Follow Windows" : name, Tag = name });
        ThemeCombo.SelectedItem = ThemeCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, settings.Theme, StringComparison.OrdinalIgnoreCase))
                                  ?? ThemeCombo.Items[0];

        FolderBox.Text = settings.ResolvedLibraryFolder;
        TrayCheck.IsChecked = settings.KeepRunningInTray;
        StartupCheck.IsChecked = StartupRegistration.IsEnabled();
        OpenLastCheck.IsChecked = settings.OpenLastSnapshotOnStart;

        DetailedLoggingCheck.IsChecked = settings.DetailedLogging;
        if (ErrorReporting.IsAvailable)
        {
            CrashReportsCheck.IsChecked = settings.SendCrashReports == true;
            CrashReportsHelp.Text = "Technical details only (error, app and Windows version, displays). Never screenshots, notes, titles, your user name or computer name.";
        }
        else
        {
            CrashReportsCheck.IsEnabled = false;
            CrashReportsHelp.Text = "Not available in this build. Crash reports are kept on this PC and go into support bundles.";
        }

        Validate();
    }

    private void Support_Click(object sender, RoutedEventArgs e) => _app.OpenSupport();

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => SupportActions.OpenLogFolder();

    private void BuildHotkeyRows(AppSettings settings)
    {
        for (int i = 0; i < Actions.Length; i++)
        {
            var (action, label) = Actions[i];
            HotkeyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(text, i);

            var box = new HotkeyBox { Hotkey = settings.GetHotkey(action), Margin = new Thickness(0, 4, 0, 4) };
            box.HotkeyChanged += (_, _) => Validate();
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);

            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
            Grid.SetRow(status, i);
            Grid.SetColumn(status, 2);

            HotkeyGrid.Children.Add(text);
            HotkeyGrid.Children.Add(box);
            HotkeyGrid.Children.Add(status);
            _rows[action] = (box, status);
        }
    }

    /// <summary>Checks duplicates and whether Windows will accept each shortcut (another app may own it).</summary>
    private bool Validate()
    {
        bool ok = true;
        var seen = new Dictionary<Hotkey, HotkeyAction>();
        foreach (var (action, (box, status)) in _rows)
        {
            var hotkey = box.Hotkey;
            string? error = null;
            if (hotkey.IsEmpty)
            {
                status.Text = "Off";
                status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Muted");
                continue;
            }
            if (seen.TryGetValue(hotkey, out var other)) error = $"Also used for “{Actions.First(a => a.Action == other).Label}”.";
            else error = _app.TestHotkey(hotkey);
            seen.TryAdd(hotkey, action);

            if (error != null)
            {
                ok = false;
                status.Text = "⚠ " + error;
                status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
            }
            else
            {
                status.Text = "✓ Available";
                status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Muted");
            }
        }
        FooterText.Text = ok ? "" : "Some shortcuts can't be used. They will stay inactive until you change them.";
        return ok;
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (action, gesture) in AppSettings.DefaultHotkeys)
            if (_rows.TryGetValue(action, out var row)) row.Box.Hotkey = Hotkey.Parse(gesture);
        Validate();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is ComboBoxItem { Tag: string name }) ThemeManager.Apply(name);
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where snapshots are stored", InitialDirectory = FolderBox.Text };
        if (dialog.ShowDialog(this) == true)
        {
            _libraryFolder = dialog.FolderName;
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void DefaultFolder_Click(object sender, RoutedEventArgs e)
    {
        _libraryFolder = null;
        FolderBox.Text = AppSettings.DefaultLibraryFolder;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(FolderBox.Text);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FolderBox.Text}\"") { UseShellExecute = true });
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = _app.Settings;
        foreach (var (action, (box, _)) in _rows) settings.Hotkeys[action] = box.Hotkey.ToString();
        if (ThemeCombo.SelectedItem is ComboBoxItem { Tag: string theme }) settings.Theme = theme;
        settings.KeepRunningInTray = TrayCheck.IsChecked == true;
        settings.OpenLastSnapshotOnStart = OpenLastCheck.IsChecked == true;
        StartupRegistration.Set(StartupCheck.IsChecked == true);
        if (settings.DetailedLogging != (DetailedLoggingCheck.IsChecked == true))
        {
            settings.DetailedLogging = DetailedLoggingCheck.IsChecked == true;
            Log.MinimumLevel = settings.DetailedLogging ? LogLevel.Debug : LogLevel.Info;
            Log.Info("settings", $"Detailed logging {(settings.DetailedLogging ? "on" : "off")}.");
        }
        if (ErrorReporting.IsAvailable && (settings.SendCrashReports == true) != (CrashReportsCheck.IsChecked == true))
            _app.SetCrashReporting(CrashReportsCheck.IsChecked == true);

        string newFolder = string.IsNullOrWhiteSpace(_libraryFolder) ? AppSettings.DefaultLibraryFolder : _libraryFolder;
        bool folderChanged = !string.Equals(Path.GetFullPath(newFolder).TrimEnd('\\'), Path.GetFullPath(settings.ResolvedLibraryFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (folderChanged)
        {
            var answer = MessageBox.Show(this,
                $"Move your existing snapshots to\n{newFolder}?\n\nChoose No to start with the snapshots already in that folder (your current ones stay where they are).",
                "Change library folder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            IsEnabled = false;
            try
            {
                await _app.ChangeLibraryFolderAsync(_libraryFolder, answer == MessageBoxResult.Yes);
            }
            catch (Exception ex)
            {
                IsEnabled = true;
                MessageBox.Show(this, $"Could not change the library folder.\n\n{ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        settings.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(ThemeManager.CurrentName, _originalTheme, StringComparison.OrdinalIgnoreCase)) ThemeManager.Apply(_originalTheme);
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DialogResult != true && !string.Equals(ThemeManager.CurrentName, _originalTheme, StringComparison.OrdinalIgnoreCase))
            ThemeManager.Apply(_originalTheme);
    }
}

/// <summary>"Start with Windows" via the per-user Run key.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SnapshotNotetaker";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
            else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Could not update the startup registration.", ex);
        }
    }
}
