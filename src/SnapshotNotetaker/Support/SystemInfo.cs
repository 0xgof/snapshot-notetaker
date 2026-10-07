using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using SnapshotNotetaker.Capture;
using SnapshotNotetaker.Themes;

namespace SnapshotNotetaker.Support;

/// <summary>Technical facts about the machine and app state that help diagnose a problem. No user content.</summary>
public static class SystemInfo
{
    public static List<(string Key, string Value)> Collect(bool includeLibrarySize = false)
    {
        var items = new List<(string, string)>();
        void Add(string key, Func<string> value)
        {
            try { items.Add((key, value())); }
            catch (Exception ex) { items.Add((key, $"<unavailable: {ex.GetType().Name}>")); }
        }

        Add("App version", () => $"{BuildInfo.Version} ({BuildInfo.DeploymentEnvironment})");
        Add("Session", () => Log.SessionId);
        Add("Started", () => Process.GetCurrentProcess().StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Add("Windows", () => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Add(".NET", () => $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        Add("Culture", () => $"{CultureInfo.CurrentCulture.Name}, UI {CultureInfo.CurrentUICulture.Name}");
        Add("Memory", () => $"{Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB working set");
        Add("Graphics", () => (RenderCapability.Tier >> 16) switch { 2 => "hardware accelerated (tier 2)", 1 => "partially accelerated (tier 1)", _ => "software rendering (tier 0)" });
        Add("Displays", () =>
        {
            var monitors = ScreenInfo.GetMonitors();
            return string.Join("; ", monitors.Select(m => $"{m.Bounds.Width}×{m.Bounds.Height} @ {m.DpiScale * 100:0}%{(m.IsPrimary ? " primary" : "")} at {m.Bounds.X},{m.Bounds.Y}"));
        });
        Add("Theme", () => ThemeManager.CurrentName == ThemeManager.SystemThemeName ? $"System ({ThemeManager.Current.Name})" : ThemeManager.CurrentName);

        if (Application.Current is App app)
        {
            Add("Library", () =>
            {
                string text = $"{app.Library?.Entries.Count ?? 0} snapshots in {Privacy.Scrub(app.Settings.ResolvedLibraryFolder)}";
                if (includeLibrarySize && Directory.Exists(app.Settings.ResolvedLibraryFolder))
                {
                    long bytes = new DirectoryInfo(app.Settings.ResolvedLibraryFolder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    text += $", {bytes / (1024.0 * 1024.0):0.0} MB";
                }
                return text;
            });
            Add("Shortcuts", () => string.Join("; ", Enum.GetValues<HotkeyAction>().Select(a =>
            {
                var hotkey = app.Settings.GetHotkey(a);
                string state = hotkey.IsEmpty ? "off" : app.HotkeyErrors.TryGetValue(a, out var error) ? $"FAILED: {error}" : "ok";
                return $"{a}={(hotkey.IsEmpty ? "-" : hotkey.ToString())} ({state})";
            })));
            Add("Options", () => $"expand={app.Settings.EffectiveNotesLayout}, tray={app.Settings.KeepRunningInTray}, " +
                                 $"detailedLog={app.Settings.DetailedLogging}, crashReports={(ErrorReporting.IsAvailable ? (ErrorReporting.IsEnabled ? "on" : "off") : "not configured")}");
        }
        return items;
    }

    public static string Describe(bool includeLibrarySize = false)
    {
        var items = Collect(includeLibrarySize);
        int width = items.Max(i => i.Key.Length) + 2;
        return string.Join(Environment.NewLine, items.Select(i => i.Key.PadRight(width) + i.Value));
    }
}
