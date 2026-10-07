using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using SnapshotNotetaker.Capture;
using SnapshotNotetaker.Editor;
using SnapshotNotetaker.IO;
using SnapshotNotetaker.Model;
using SnapshotNotetaker.Support;

namespace SnapshotNotetaker.Settings;

/// <summary>Style applied to newly drawn shapes. Sizes are nominal (multiplied by the snapshot's DPI scale).</summary>
public sealed class ShapeDefaults
{
    public Color StrokeColor { get; set; } = Color.FromRgb(0xE5, 0x39, 0x35);
    public double StrokeWidth { get; set; } = 3;
    public StrokeDash Dash { get; set; }
    public double FillOpacity { get; set; }
    public bool StrokeOutline { get; set; }
    public LabelMode LabelMode { get; set; } = LabelMode.Number;
    public LabelStyle Label { get; set; } = new();
}

public sealed class AppSettings
{
    public static readonly string AppDataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapshotNotetaker");

    public static readonly string DefaultLibraryFolder = Path.Combine(AppDataFolder, "Snapshots");

    private static string FilePath => Path.Combine(AppDataFolder, "settings.json");

    public static Dictionary<HotkeyAction, string> DefaultHotkeys => new()
    {
        [HotkeyAction.CaptureRegion] = "PrintScreen",
        [HotkeyAction.CaptureWindow] = "Ctrl+PrintScreen",
        [HotkeyAction.CaptureScreen] = "Shift+PrintScreen",
        [HotkeyAction.CaptureAllScreens] = "Ctrl+Shift+PrintScreen",
        [HotkeyAction.ShowApp] = "",
    };

    public string Theme { get; set; } = "System";
    public string? LibraryFolder { get; set; }
    public Dictionary<HotkeyAction, string> Hotkeys { get; set; } = DefaultHotkeys;
    public bool KeepRunningInTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public bool OpenLastSnapshotOnStart { get; set; } = true;
    public bool StartMinimizedToTray { get; set; }

    public ShapeDefaults Defaults { get; set; } = new();
    public NumberingOptions Numbering { get; set; } = new();
    public EditorTool LastTool { get; set; } = EditorTool.Rectangle;

    public bool ShowLibrary { get; set; } = true;
    public bool ShowNotes { get; set; } = true;
    public double LibraryWidth { get; set; } = 240;
    public double NotesWidth { get; set; } = 330;
    /// <summary>Expanded view: the image grows to show tags and comments, and copies/exports include them.</summary>
    public bool ExpandWithNotes { get; set; }
    public NotesLayout ExpandLayout { get; set; } = NotesLayout.Margins;
    public string? LastSnapshotId { get; set; }
    public string? LastExportFolder { get; set; }

    /// <summary>Automatic crash reports: null until the user has been asked.</summary>
    public bool? SendCrashReports { get; set; }
    public bool DetailedLogging { get; set; }
    /// <summary>Random, anonymous id so crash reports from the same install can be grouped.</summary>
    public string InstallId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime LastCrashCheckUtc { get; set; }

    [JsonIgnore]
    public string ResolvedLibraryFolder => string.IsNullOrWhiteSpace(LibraryFolder) ? DefaultLibraryFolder : LibraryFolder;

    [JsonIgnore]
    public NotesLayout EffectiveNotesLayout
        => !ExpandWithNotes ? NotesLayout.None : ExpandLayout == NotesLayout.None ? NotesLayout.Margins : ExpandLayout;

    public Hotkey GetHotkey(HotkeyAction action)
        => Hotkeys.TryGetValue(action, out var text) ? Hotkey.Parse(text) : Hotkey.None;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json.Options) ?? new AppSettings();
                foreach (var (action, gesture) in DefaultHotkeys)
                    settings.Hotkeys.TryAdd(action, gesture);
                return settings;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Could not read settings; using defaults.", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json.Options));
            FileUtil.ReplaceWith(tmp, FilePath);
        }
        catch (Exception ex)
        {
            Log.Error("settings", "Could not save settings.", ex);
        }
    }
}
