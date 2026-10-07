using System.Diagnostics;
using System.IO;

namespace SnapshotNotetaker.IO;

public static class FileUtil
{
    /// <summary>How long to keep retrying while another process holds the file (antivirus scans can take seconds).</summary>
    public static TimeSpan ReplaceTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Moves a freshly written temp file over <paramref name="path"/>. Antivirus, the search indexer or an Explorer
    /// preview can hold the target open for a moment right after it was written, so retry with backoff before failing.
    /// Only called from background writers, so the wait never blocks the UI.
    /// </summary>
    public static void ReplaceWith(string tempPath, string path)
    {
        var watch = Stopwatch.StartNew();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && watch.Elapsed < ReplaceTimeout)
            {
                Thread.Sleep(Math.Min(50 * attempt, 500));
            }
        }
    }
}
