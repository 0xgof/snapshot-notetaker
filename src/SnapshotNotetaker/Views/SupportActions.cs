using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SnapshotNotetaker.Support;

namespace SnapshotNotetaker.Views;

/// <summary>Support actions shared by the Help window, the problem dialog, Settings and the Help menu.</summary>
internal static class SupportActions
{
    /// <summary>Asks where to save, builds the bundle in the background and shows it in Explorer.</summary>
    public static async Task<string?> CreateBundleAsync(Window owner, string? description = null, bool includeCurrentSnapshot = false)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save support bundle",
            Filter = "Zip archive|*.zip",
            FileName = SupportBundle.DefaultFileName,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog(owner) != true) return null;

        string systemInfo = SystemInfo.Describe(includeLibrarySize: true); // reads WPF state: collect on the UI thread
        string? snapshotFolder = includeCurrentSnapshot ? (Application.Current as App)?.CurrentSnapshotFolder : null;
        try
        {
            owner.Cursor = System.Windows.Input.Cursors.Wait;
            await Task.Run(() => SupportBundle.Create(dialog.FileName, systemInfo, description, snapshotFolder));
        }
        catch (Exception ex)
        {
            Log.Error("support", "Could not create the support bundle.", ex);
            MessageBox.Show(owner, $"The support bundle could not be created.\n\n{ex.Message}", "Snapshot Notetaker", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            owner.Cursor = null;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true });
        return dialog.FileName;
    }

    /// <summary>System summary for pasting into an email or ticket.</summary>
    public static bool CopySummary()
    {
        string text = "Snapshot Notetaker diagnostic summary" + Environment.NewLine + SystemInfo.Describe(includeLibrarySize: true);
        return TrySetClipboard(text);
    }

    public static bool TrySetClipboard(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }
        return false;
    }

    public static void OpenLogFolder()
    {
        Directory.CreateDirectory(Log.Folder);
        Log.Flush(TimeSpan.FromSeconds(1));
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.Folder}\"") { UseShellExecute = true });
    }

    public static void OpenSupportContact()
    {
        string? target = BuildInfo.SupportUrl ?? (BuildInfo.SupportEmail is { } email ? $"mailto:{email}?subject=Snapshot%20Notetaker%20{BuildInfo.Version}" : null);
        if (target != null) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
