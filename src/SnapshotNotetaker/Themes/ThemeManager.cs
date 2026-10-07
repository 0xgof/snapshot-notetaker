using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SnapshotNotetaker.Rendering;

namespace SnapshotNotetaker.Themes;

/// <summary>A UI color theme. Every brush in the UI is a DynamicResource keyed "Brush.&lt;Name&gt;".</summary>
public sealed record ThemeDefinition(
    string Name, bool IsDark,
    string Window, string Panel, string PanelAlt, string Border, string Text, string Muted,
    string Accent, string AccentText, string AccentSoft, string Hover, string Pressed,
    string Canvas, string Input, string Popup, string Danger);

public static class ThemeManager
{
    public const string SystemThemeName = "System";

    public static readonly IReadOnlyList<ThemeDefinition> Themes = new[]
    {
        new ThemeDefinition("Light", false,
            Window: "#F3F4F6", Panel: "#FFFFFF", PanelAlt: "#F7F8FA", Border: "#E2E4E8", Text: "#1F2328", Muted: "#6B7280",
            Accent: "#2563EB", AccentText: "#FFFFFF", AccentSoft: "#DCE8FD", Hover: "#EEF0F3", Pressed: "#E2E5EA",
            Canvas: "#3A3D42", Input: "#FFFFFF", Popup: "#FFFFFF", Danger: "#DC2626"),
        new ThemeDefinition("Dark", true,
            Window: "#1E1F22", Panel: "#2B2D30", PanelAlt: "#25272A", Border: "#3C3F44", Text: "#DFE1E5", Muted: "#8C9099",
            Accent: "#3B82F6", AccentText: "#FFFFFF", AccentSoft: "#1F3A61", Hover: "#383B40", Pressed: "#44474D",
            Canvas: "#151618", Input: "#1E1F22", Popup: "#2F3134", Danger: "#F87171"),
        new ThemeDefinition("Midnight", true,
            Window: "#0B1220", Panel: "#111A2E", PanelAlt: "#0E1628", Border: "#22304D", Text: "#E2E8F0", Muted: "#8A9BB8",
            Accent: "#38BDF8", AccentText: "#04121F", AccentSoft: "#0C3452", Hover: "#1A2742", Pressed: "#22345A",
            Canvas: "#060A14", Input: "#0A1222", Popup: "#131D33", Danger: "#FB7185"),
        new ThemeDefinition("Nord", true,
            Window: "#2E3440", Panel: "#3B4252", PanelAlt: "#353B49", Border: "#4C566A", Text: "#ECEFF4", Muted: "#A3ABBA",
            Accent: "#88C0D0", AccentText: "#2E3440", AccentSoft: "#41566A", Hover: "#434C5E", Pressed: "#4C566A",
            Canvas: "#242933", Input: "#2E3440", Popup: "#3B4252", Danger: "#BF616A"),
        new ThemeDefinition("Forest", true,
            Window: "#141C17", Panel: "#1C2620", PanelAlt: "#18211B", Border: "#2E3D33", Text: "#E3EDE5", Muted: "#8FA596",
            Accent: "#4ADE80", AccentText: "#06220F", AccentSoft: "#1D4A2D", Hover: "#253229", Pressed: "#2E3E33",
            Canvas: "#0C110E", Input: "#121915", Popup: "#1F2A23", Danger: "#F87171"),
        new ThemeDefinition("Sepia", false,
            Window: "#F2ECE2", Panel: "#FBF7F0", PanelAlt: "#F4EEE4", Border: "#E0D5C3", Text: "#3B2F24", Muted: "#8A7B69",
            Accent: "#B45309", AccentText: "#FFFFFF", AccentSoft: "#F5E1C4", Hover: "#EFE5D6", Pressed: "#E6D8C3",
            Canvas: "#5A4E42", Input: "#FFFDF8", Popup: "#FFFBF4", Danger: "#B91C1C"),
        new ThemeDefinition("Rose", false,
            Window: "#FAF1F3", Panel: "#FFFFFF", PanelAlt: "#FCF5F7", Border: "#F0DCE2", Text: "#33232A", Muted: "#8F6F7B",
            Accent: "#DB2777", AccentText: "#FFFFFF", AccentSoft: "#FBDCEA", Hover: "#F8E8ED", Pressed: "#F1D7E0",
            Canvas: "#3D3036", Input: "#FFFFFF", Popup: "#FFFFFF", Danger: "#BE123C"),
        new ThemeDefinition("High contrast", true,
            Window: "#000000", Panel: "#000000", PanelAlt: "#0A0A0A", Border: "#FFFFFF", Text: "#FFFFFF", Muted: "#D0D0D0",
            Accent: "#FFD400", AccentText: "#000000", AccentSoft: "#3D3300", Hover: "#1F1F1F", Pressed: "#383838",
            Canvas: "#000000", Input: "#000000", Popup: "#000000", Danger: "#FF6B6B"),
    };

    private static bool _listening;

    public static string CurrentName { get; private set; } = SystemThemeName;
    public static ThemeDefinition Current { get; private set; } = Themes[0];

    public static IEnumerable<string> Names => new[] { SystemThemeName }.Concat(Themes.Select(t => t.Name));

    public static event EventHandler? ThemeChanged;

    public static void Apply(string? name)
    {
        CurrentName = string.IsNullOrWhiteSpace(name) ? SystemThemeName : name;
        var theme = CurrentName == SystemThemeName
            ? (SystemPrefersDark() ? Find("Dark") : Find("Light"))
            : Find(CurrentName);
        Current = theme;

        var resources = Application.Current.Resources;
        void Set(string key, string hex)
        {
            ColorUtil.TryParse(hex, out var color);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources["Brush." + key] = brush;
            resources["Color." + key] = color;
        }

        Set("Window", theme.Window);
        Set("Panel", theme.Panel);
        Set("PanelAlt", theme.PanelAlt);
        Set("Border", theme.Border);
        Set("Text", theme.Text);
        Set("Muted", theme.Muted);
        Set("Accent", theme.Accent);
        Set("AccentText", theme.AccentText);
        Set("AccentSoft", theme.AccentSoft);
        Set("Hover", theme.Hover);
        Set("Pressed", theme.Pressed);
        Set("Canvas", theme.Canvas);
        Set("Input", theme.Input);
        Set("Popup", theme.Popup);
        Set("Danger", theme.Danger);

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);

        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && CurrentName == SystemThemeName)
                    Application.Current.Dispatcher.BeginInvoke(() => Apply(SystemThemeName));
            };
        }
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static ThemeDefinition Find(string name)
        => Themes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

    /// <summary>Dark title bar for dark themes (Windows 10 20H1+ / 11).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            window.SourceInitialized -= OnSourceInitialized;
            window.SourceInitialized += OnSourceInitialized;
            return;
        }
        int dark = Current.IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int)); // pre-20H1 attribute id
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window w) ApplyTitleBar(w);
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
