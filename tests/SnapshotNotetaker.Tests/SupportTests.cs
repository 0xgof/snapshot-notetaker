using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using SnapshotNotetaker.Support;
using Xunit;

namespace SnapshotNotetaker.Tests;

internal static class TestEnvironment
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), $"sn-tests-{Guid.NewGuid():N}");

    /// <summary>Keep test runs out of the real %LOCALAPPDATA% logs and crash folders.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        Log.Configure(Path.Combine(Root, "logs"));
        CrashHandler.Configure(Path.Combine(Root, "crashes"));
    }
}

public class CompatibilityTests
{
    /// <summary>document.json exactly as version 0.1 wrote it: must keep loading after serializer changes.</summary>
    [Fact]
    public void ReadsDocumentJsonWrittenByVersion01()
    {
        const string json = """
        {
          "version": 1,
          "title": "Dashboard review",
          "source": "Region 1600×1000",
          "capturedAt": "2026-10-07T14:32:00",
          "modifiedAt": "2026-10-07T14:40:00",
          "captureScale": 1.5,
          "width": 1600,
          "height": 1000,
          "numbering": { "format": "UpperRoman", "start": 2, "prefix": "#", "suffix": "" },
          "annotations": [
            {
              "id": "7d1c4c4e-1111-4a9b-9a59-0d6a3c2b9f10",
              "kind": "Spline",
              "bounds": [0, 0, 0, 0],
              "points": [[1, 2], [30.5, 4], [12, 40]],
              "isClosed": true,
              "strokeColor": "#FFFB8C00",
              "strokeWidth": 3,
              "dash": "Dashed",
              "fillOpacity": 0.15,
              "strokeOutline": false,
              "labelMode": "NumberAndTag",
              "tag": "Status",
              "label": { "shape": "Callout", "anchor": "Right", "placement": "OnEdge", "scheme": "Custom",
                         "customFill": "#FFFFD600", "customText": "#FF111111", "fontSize": 15, "bold": true, "outline": true, "shadow": false },
              "labelOffset": [5, -3],
              "comment": "Colours are hard to tell apart"
            }
          ]
        }
        """;
        var file = System.Text.Json.JsonSerializer.Deserialize<IO.SnapshotFile>(json, IO.Json.Options)!;
        Assert.Equal("Dashboard review", file.Title);
        Assert.Equal(1.5, file.CaptureScale);
        Assert.Equal(Model.NumberFormat.UpperRoman, file.Numbering.Format);
        Assert.Equal("#", file.Numbering.Prefix);
        var a = Assert.Single(file.Annotations);
        Assert.Equal(Model.ShapeKind.Spline, a.Kind);
        Assert.Equal(3, a.Points.Length);
        Assert.Equal(30.5, a.Points[1].X);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xFB, 0x8C, 0x00), a.StrokeColor);
        Assert.Equal(Model.StrokeDash.Dashed, a.Dash);
        Assert.Equal(Model.LabelShape.Callout, a.Label.Shape);
        Assert.Equal(Model.LabelColorScheme.Custom, a.Label.Scheme);
        Assert.False(a.Label.Shadow);
        Assert.Equal(new System.Windows.Vector(5, -3), a.LabelOffset);
        Assert.Equal("Colours are hard to tell apart", a.Comment);

        // And it is written back with the same names.
        string again = System.Text.Json.JsonSerializer.Serialize(file, IO.Json.Options);
        Assert.Contains("\"labelMode\": \"NumberAndTag\"", again);
        Assert.Contains("\"captureScale\": 1.5", again);
    }

    [Fact]
    public void ReadsSettingsWrittenByVersion01()
    {
        const string json = """
        { "theme": "Nord", "hotkeys": { "CaptureRegion": "Ctrl+Shift+S" }, "expandWithNotes": true, "expandLayout": "Right",
          "defaults": { "strokeColor": "#FF8E24AA", "strokeWidth": 4, "label": { "shape": "Circle" } }, "lastTool": "Spline" }
        """;
        var s = System.Text.Json.JsonSerializer.Deserialize<Settings.AppSettings>(json, IO.Json.Options)!;
        Assert.Equal("Nord", s.Theme);
        Assert.Equal("Ctrl+Shift+S", s.Hotkeys[Capture.HotkeyAction.CaptureRegion]);
        Assert.Equal(Model.NotesLayout.Right, s.EffectiveNotesLayout);
        Assert.Equal(4, s.Defaults.StrokeWidth);
        Assert.Equal(Model.LabelShape.Circle, s.Defaults.Label.Shape);
        Assert.Equal(Editor.EditorTool.Spline, s.LastTool);
    }
}

public class SupportTests
{
    [Fact]
    public void PrivacyMasksUserProfileUserNameAndComputer()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string text = $"Access to '{profile}\\AppData\\x.json' denied on {Environment.MachineName}; also C:\\Users\\{Environment.UserName}\\Desktop";
        string scrubbed = Privacy.Scrub(text);
        Assert.DoesNotContain(profile, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\" + Environment.UserName + "\\", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%\\AppData\\x.json", scrubbed);
        Assert.Contains("<computer>", scrubbed);
    }

    [Fact]
    public void LogRollsOverAndKeepsALimitedNumberOfFiles()
    {
        string folder = Path.Combine(TestEnvironment.Root, "roll-" + Guid.NewGuid().ToString("N"));
        var (oldFolder, oldMax, oldKeep) = (Log.Folder, Log.MaxFileBytes, Log.KeepFiles);
        try
        {
            Log.Configure(folder, maxFileBytes: 2_000, keepFiles: 3);
            for (int i = 0; i < 60; i++)
            {
                Log.Info("test", $"entry {i} " + new string('x', 120));
                if (i % 5 == 0) Log.Flush(TimeSpan.FromSeconds(5));
            }
            Assert.True(Log.Flush(TimeSpan.FromSeconds(5)));
            var files = Directory.GetFiles(folder, "app*.log");
            Assert.InRange(files.Length, 2, 3);
            Assert.Contains(files, f => Path.GetFileName(f) == "app.log");
            Assert.Contains("entry 59", File.ReadAllText(Path.Combine(folder, "app.log")));
        }
        finally
        {
            Log.Configure(oldFolder, oldMax, oldKeep);
        }
    }

    [Fact]
    public void LogEntriesAreScrubbedAndKeptForCrashReports()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Log.Warn("test", $"Could not open {profile}\\secret.png", new IOException("disk"));
        var last = Log.RecentEntries()[^1];
        Assert.Contains("WRN [test]", last);
        Assert.Contains("%USERPROFILE%", last);
        Assert.Contains("System.IO.IOException: disk", last);
    }

    [Fact]
    public void CrashReportContainsTheErrorSystemInfoAndRecentLog()
    {
        Log.Info("test", "something happened just before the crash");
        Exception ex;
        try { throw new InvalidOperationException("boom"); } catch (Exception e) { ex = e; }
        string? path = CrashHandler.WriteReport(ex, fatal: true);
        Assert.NotNull(path);
        string text = File.ReadAllText(path!);
        Assert.Contains("InvalidOperationException: boom", text);
        Assert.Contains("App version", text);
        Assert.Contains("something happened just before the crash", text);
        Assert.NotNull(CrashHandler.FatalSince(DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public void SupportBundleLeavesSnapshotsOutUnlessAsked()
    {
        string dir = Path.Combine(TestEnvironment.Root, "bundle-" + Guid.NewGuid().ToString("N"));
        string snapshot = Path.Combine(dir, "snap");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "image.png"), "png");
        File.WriteAllText(Path.Combine(snapshot, "document.json"), "{}");
        Log.Info("test", "bundle test entry");

        string plain = Path.Combine(dir, "plain.zip"), full = Path.Combine(dir, "full.zip");
        SupportBundle.Create(plain, "system info here", "It crashed when I pressed X", snapshotFolder: null);
        SupportBundle.Create(full, "system info here", null, snapshotFolder: snapshot);

        using (var zip = ZipFile.OpenRead(plain))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("README.txt", names);
            Assert.Contains("system-info.txt", names);
            Assert.Contains("description.txt", names);
            Assert.Contains(names, n => n.StartsWith("logs/app"));
            Assert.DoesNotContain(names, n => n.StartsWith("snapshot/"));
        }
        using (var zip = ZipFile.OpenRead(full))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("snapshot/image.png", names);
            Assert.Contains("snapshot/document.json", names);
            Assert.DoesNotContain("description.txt", names);
        }
    }

    [Fact]
    public void CrashReportingStaysOffWithoutADsn()
    {
        if (ErrorReporting.IsAvailable) return; // a DSN was supplied to this build/environment
        ErrorReporting.Apply(consent: true, installId: "test");
        Assert.False(ErrorReporting.IsEnabled);
        Assert.False(ErrorReporting.SendOnce(new Exception("x")));
    }
}
