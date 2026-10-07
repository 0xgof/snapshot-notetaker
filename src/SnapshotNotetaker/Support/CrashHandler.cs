using System.Globalization;
using System.IO;
using System.Text;
using SnapshotNotetaker.Settings;

namespace SnapshotNotetaker.Support;

/// <summary>
/// Catches failures on every thread and writes a crash report (exception, system info, recent log) to
/// %LOCALAPPDATA%\SnapshotNotetaker\crashes. UI-thread errors are handled by the app (it keeps running);
/// background-thread crashes end the process, so their report is flagged "-fatal" and surfaced on the next start.
/// </summary>
public static class CrashHandler
{
    private const int KeepReports = 25;
    private static bool _installed;

    public static string Folder { get; private set; } = Path.Combine(AppSettings.AppDataFolder, "crashes");

    internal static void Configure(string folder) => Folder = folder;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error");
            Log.Fatal("crash", "Unhandled exception on a background thread; the app is closing.", ex);
            WriteReport(ex, fatal: true);
            Log.Flush(TimeSpan.FromSeconds(2));
            ErrorReporting.Flush(); // Sentry records AppDomain crashes on its own; make sure it is sent
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("crash", "Unobserved background task exception.", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Writes a report and returns its path (null if even that failed).</summary>
    public static string? WriteReport(Exception ex, bool fatal)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}{(fatal ? "-fatal" : "")}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"Snapshot Notetaker {(fatal ? "crash" : "error")} report");
            sb.AppendLine($"Time: {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
            sb.AppendLine();
            sb.AppendLine("== Exception ==");
            sb.AppendLine(Privacy.Scrub(ex.ToString()));
            sb.AppendLine();
            sb.AppendLine("== System ==");
            sb.AppendLine(SystemInfo.Describe());
            sb.AppendLine();
            sb.AppendLine("== Recent log ==");
            foreach (var entry in Log.RecentEntries().TakeLast(200)) sb.AppendLine(entry);
            File.WriteAllText(path, sb.ToString());
            Prune();
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<FileInfo> Reports()
        => Directory.Exists(Folder)
            ? new DirectoryInfo(Folder).EnumerateFiles("crash-*.txt").OrderByDescending(f => f.LastWriteTimeUtc)
            : Enumerable.Empty<FileInfo>();

    /// <summary>The newest report of a crash that closed the app after <paramref name="sinceUtc"/>.</summary>
    public static FileInfo? FatalSince(DateTime sinceUtc)
        => Reports().FirstOrDefault(f => f.Name.EndsWith("-fatal.txt", StringComparison.OrdinalIgnoreCase) && f.LastWriteTimeUtc > sinceUtc);

    private static void Prune()
    {
        foreach (var old in Reports().Skip(KeepReports))
        {
            try { old.Delete(); } catch { /* best effort */ }
        }
    }
}
