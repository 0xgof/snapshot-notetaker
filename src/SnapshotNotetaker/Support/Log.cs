using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SnapshotNotetaker.Settings;

namespace SnapshotNotetaker.Support;

public enum LogLevel { Debug, Info, Warn, Error, Fatal }

/// <summary>
/// Local, rolling, thread-safe log (logs\app.log, app.1.log … app.4.log). Entries are written on a background
/// thread so logging never stalls the UI. Messages are meant to be shared with support: never put user content
/// in them (snapshot titles, comments, other apps' window titles, file names). Paths are scrubbed automatically.
/// </summary>
public static class Log
{
    private const int RecentCapacity = 500;
    private static readonly BlockingCollection<string> Pending = new(new ConcurrentQueue<string>(), 20_000);
    private static readonly LinkedList<string> Recent = new();
    private static readonly object RecentGate = new();
    private static readonly object StartGate = new();
    private static Thread? _writer;
    private static int _inFlight;

    public static string Folder { get; private set; } = Path.Combine(AppSettings.AppDataFolder, "logs");
    public static long MaxFileBytes { get; private set; } = 2_000_000;
    public static int KeepFiles { get; private set; } = 5;
    public static string CurrentFile => Path.Combine(Folder, "app.log");

    /// <summary>Random id of this run; shown in Help so a user's report can be matched to their log.</summary>
    public static string SessionId { get; } = Guid.NewGuid().ToString("N")[..8];

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Raised for entries at Info or above (feeds crash-report breadcrumbs).</summary>
    public static event Action<LogLevel, string, string>? Written;

    internal static void Configure(string folder, long maxFileBytes = 2_000_000, int keepFiles = 5)
    {
        Flush(TimeSpan.FromSeconds(2));
        Folder = folder;
        MaxFileBytes = maxFileBytes;
        KeepFiles = Math.Max(1, keepFiles);
    }

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);
    public static void Warn(string category, string message, Exception? ex = null) => Write(LogLevel.Warn, category, message, ex);
    public static void Error(string category, string message, Exception? ex = null) => Write(LogLevel.Error, category, message, ex);
    public static void Fatal(string category, string message, Exception? ex = null) => Write(LogLevel.Fatal, category, message, ex);

    /// <summary>Times an operation: <c>using var t = Log.Time("capture", "Region capture"); … t.Detail = "1280×720";</c></summary>
    public static Timing Time(string category, string operation, LogLevel level = LogLevel.Info) => new(category, operation, level);

    public sealed class Timing : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly string _category, _operation;
        private readonly LogLevel _level;
        private bool _done;

        internal Timing(string category, string operation, LogLevel level)
        {
            _category = category;
            _operation = operation;
            _level = level;
        }

        /// <summary>Extra result info appended to the entry (sizes, counts — no user content).</summary>
        public string? Detail { get; set; }

        /// <summary>Set when the operation was cancelled or failed, to say so instead of "took".</summary>
        public string? Outcome { get; set; }

        public long ElapsedMilliseconds => _watch.ElapsedMilliseconds;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            string detail = string.IsNullOrEmpty(Detail) ? "" : $" ({Detail})";
            Write(_level, _category, $"{_operation} {Outcome ?? "took"} {_watch.ElapsedMilliseconds} ms{detail}", null);
        }
    }

    /// <summary>Waits until queued entries are on disk.</summary>
    public static bool Flush(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (Volatile.Read(ref _inFlight) > 0 && watch.Elapsed < timeout) Thread.Sleep(10);
        return Volatile.Read(ref _inFlight) == 0;
    }

    /// <summary>The last few hundred entries of this session (for crash reports).</summary>
    public static IReadOnlyList<string> RecentEntries()
    {
        lock (RecentGate) return Recent.ToList();
    }

    public static IEnumerable<string> Files()
        => Directory.Exists(Folder) ? Directory.EnumerateFiles(Folder, "app*.log").OrderBy(f => f) : Enumerable.Empty<string>();

    private static void Write(LogLevel level, string category, string message, Exception? ex)
    {
        if (level < MinimumLevel) return;
        message = Privacy.Scrub(message);
        var sb = new StringBuilder(160)
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(Tag(level))
            .Append(" [").Append(category).Append("] ")
            .Append(message);
        if (ex != null) sb.AppendLine().Append(Privacy.Scrub(ex.ToString()));
        string entry = sb.ToString();

        lock (RecentGate)
        {
            Recent.AddLast(entry);
            if (Recent.Count > RecentCapacity) Recent.RemoveFirst();
        }

        EnsureWriter();
        Interlocked.Increment(ref _inFlight);
        if (!Pending.TryAdd(entry)) Interlocked.Decrement(ref _inFlight);

        if (level >= LogLevel.Info)
        {
            try { Written?.Invoke(level, category, message); }
            catch { /* a listener must never break logging */ }
        }
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "FTL",
    };

    private static void EnsureWriter()
    {
        if (_writer != null) return;
        lock (StartGate)
        {
            if (_writer != null) return;
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "LogWriter", Priority = ThreadPriority.BelowNormal };
            _writer.Start();
        }
    }

    private static void WriterLoop()
    {
        var batch = new List<string>();
        foreach (var first in Pending.GetConsumingEnumerable())
        {
            batch.Clear();
            batch.Add(first);
            while (batch.Count < 500 && Pending.TryTake(out var more)) batch.Add(more);
            try
            {
                AppendBatch(batch);
            }
            catch
            {
                // Disk full, folder deleted… logging must never take the app down.
            }
            finally
            {
                Interlocked.Add(ref _inFlight, -batch.Count);
            }
        }
    }

    private static void AppendBatch(List<string> entries)
    {
        Directory.CreateDirectory(Folder);
        RotateIfNeeded();
        using var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var entry in entries) writer.WriteLine(entry);
    }

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(CurrentFile);
        if (!info.Exists || info.Length < MaxFileBytes) return;
        for (int i = KeepFiles - 1; i >= 1; i--)
        {
            string from = i == 1 ? CurrentFile : Path.Combine(Folder, $"app.{i - 1}.log");
            string to = Path.Combine(Folder, $"app.{i}.log");
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
        }
        if (KeepFiles == 1) File.Delete(CurrentFile);
    }
}
