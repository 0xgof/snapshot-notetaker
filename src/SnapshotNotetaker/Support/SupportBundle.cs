using System.IO;
using System.IO.Compression;
using System.Text;
using SnapshotNotetaker.IO;
using SnapshotNotetaker.Settings;

namespace SnapshotNotetaker.Support;

/// <summary>
/// One zip a user can send to support: logs, crash reports, system info, settings (personal paths masked),
/// an optional description, and — only when the user asks — the snapshot the problem is about.
/// </summary>
public static class SupportBundle
{
    public static string DefaultFileName => $"SnapshotNotetaker-support-{DateTime.Now:yyyyMMdd-HHmm}.zip";

    /// <param name="systemInfo">Collected on the UI thread beforehand (it reads WPF state).</param>
    /// <param name="snapshotFolder">Library folder of the snapshot to include, or null.</param>
    public static void Create(string zipPath, string systemInfo, string? description, string? snapshotFolder)
    {
        Log.Flush(TimeSpan.FromSeconds(2));
        string tmp = zipPath + ".tmp";
        using (var stream = File.Create(tmp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            AddText(zip, "README.txt", Readme(snapshotFolder != null));
            AddText(zip, "system-info.txt", systemInfo);
            if (!string.IsNullOrWhiteSpace(description)) AddText(zip, "description.txt", description.Trim());

            foreach (var file in Log.Files()) AddFile(zip, file, "logs/" + Path.GetFileName(file));
            string legacyLog = Path.Combine(AppSettings.AppDataFolder, "app.log"); // pre-0.2 location
            if (File.Exists(legacyLog)) AddFile(zip, legacyLog, "logs/legacy-app.log");

            foreach (var report in CrashHandler.Reports().Take(10)) AddFile(zip, report.FullName, "crashes/" + report.Name);

            string settings = Path.Combine(AppSettings.AppDataFolder, "settings.json");
            if (File.Exists(settings)) AddText(zip, "settings.json", Privacy.Scrub(ReadShared(settings)));

            if (snapshotFolder != null && Directory.Exists(snapshotFolder))
            {
                foreach (var name in new[] { SnapshotLibrary.ImageFileName, SnapshotLibrary.DocumentFileName })
                {
                    string path = Path.Combine(snapshotFolder, name);
                    if (File.Exists(path)) AddFile(zip, path, "snapshot/" + name);
                }
            }
        }
        FileUtil.ReplaceWith(tmp, zipPath);
        Log.Info("support", $"Support bundle created{(snapshotFolder != null ? " (with the current snapshot)" : "")}.");
    }

    private static string Readme(bool withSnapshot) => new StringBuilder()
        .AppendLine("Snapshot Notetaker support bundle")
        .AppendLine()
        .AppendLine("system-info.txt   app version, Windows and .NET versions, displays, shortcuts, options")
        .AppendLine("logs/             recent activity and errors (technical events only)")
        .AppendLine("crashes/          crash and error reports")
        .AppendLine("settings.json     app settings, with personal paths masked")
        .AppendLine("description.txt   what you wrote about the problem (if anything)")
        .AppendLine(withSnapshot
            ? "snapshot/         the snapshot you chose to include: image.png and its notes (document.json)"
            : "No screenshots or notes are included.")
        .ToString();

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void AddFile(ZipArchive zip, string path, string name)
    {
        try
        {
            var entry = zip.CreateEntry(name, name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = entry.Open();
            source.CopyTo(target);
        }
        catch (Exception ex)
        {
            Log.Warn("support", $"Could not add {name} to the support bundle.", ex);
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
