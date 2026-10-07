using System.IO;

namespace SnapshotNotetaker.IO;

public static class FileUtil
{
    /// <summary>
    /// Moves a freshly written temp file over <paramref name="path"/>. Antivirus, the search indexer or an Explorer
    /// preview can hold the target open for a moment right after it was written, so retry briefly before failing.
    /// </summary>
    public static void ReplaceWith(string tempPath, string path)
    {
        const int attempts = 8;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < attempts)
            {
                Thread.Sleep(40 * attempt);
            }
        }
    }
}
