using System.Windows;
using System.Windows.Media.Imaging;
using SnapshotNotetaker.Interop;

namespace SnapshotNotetaker.Capture;

public sealed record CaptureResult(BitmapSource Image, double DpiScale, string Source);

/// <summary>Runs captures: hides the app's own windows, grabs the screen, and (for region/window) shows the picker.</summary>
public sealed class CaptureService
{
    private bool _busy;

    public bool IsBusy => _busy;

    public Task<CaptureResult?> CaptureRegionAsync() => CaptureInteractiveAsync(CaptureMode.Region);

    public Task<CaptureResult?> CaptureWindowAsync() => CaptureInteractiveAsync(CaptureMode.Window);

    /// <summary>Captures one display: <paramref name="device"/> (by device name) or the one under the cursor.</summary>
    public Task<CaptureResult?> CaptureScreenAsync(string? device = null) => CaptureDisplaysAsync(allMonitors: false, device);

    public Task<CaptureResult?> CaptureAllScreensAsync() => CaptureDisplaysAsync(allMonitors: true, null);

    private async Task<CaptureResult?> CaptureDisplaysAsync(bool allMonitors, string? device)
    {
        if (_busy) return null;
        _busy = true;
        var hidden = await HideAppWindowsAsync();
        try
        {
            var monitors = ScreenInfo.GetMonitors();
            if (allMonitors || monitors.Count == 0)
            {
                var v = ScreenInfo.VirtualBounds();
                double scale = monitors.Count > 0 ? monitors.Max(m => m.DpiScale) : 1;
                return new CaptureResult(ScreenCapture.CaptureScreen(v), scale, monitors.Count > 1 ? "All displays" : "Full screen");
            }
            var (x, y) = ScreenInfo.CursorPosition();
            var monitor = monitors.FirstOrDefault(m => m.DeviceName == device) ?? ScreenInfo.MonitorAt(monitors, x, y);
            return new CaptureResult(ScreenCapture.CaptureScreen(monitor.Bounds), monitor.DpiScale,
                monitors.Count > 1 ? $"Display {monitor.Index}" : "Full screen");
        }
        finally
        {
            RestoreWindows(hidden);
            _busy = false;
        }
    }

    public CaptureResult? CaptureSpecificWindow(WindowInfo window)
    {
        var image = ScreenCapture.CaptureWindow(window.Handle);
        if (image == null) return null;
        var monitors = ScreenInfo.GetMonitors();
        double scale = monitors.Count > 0 ? ScreenInfo.MonitorForRect(monitors, window.Bounds).DpiScale : 1;
        return new CaptureResult(image, scale, string.IsNullOrWhiteSpace(window.Title) ? "Window" : window.Title);
    }

    private async Task<CaptureResult?> CaptureInteractiveAsync(CaptureMode mode)
    {
        if (_busy) return null;
        _busy = true;
        var hidden = await HideAppWindowsAsync();
        var overlays = new List<CaptureOverlayWindow>();
        try
        {
            var monitors = ScreenInfo.GetMonitors();
            var virtualBounds = ScreenInfo.VirtualBounds();
            var windows = WindowFinder.GetWindows();
            var screen = ScreenCapture.CaptureScreen(virtualBounds);
            var session = new CaptureSession(screen, virtualBounds, monitors, windows, mode);

            foreach (var monitor in monitors)
            {
                var overlay = new CaptureOverlayWindow(session, monitor);
                overlays.Add(overlay);
                overlay.Show();
            }
            var (cx, cy) = ScreenInfo.CursorPosition();
            (overlays.FirstOrDefault(o => o.Monitor.Bounds.Contains(cx, cy)) ?? overlays.FirstOrDefault())?.ActivateForInput();

            var selection = await session.Result;
            foreach (var o in overlays) o.Close();
            overlays.Clear();
            if (selection == null) return null;

            var rect = selection.Rect.Intersect(virtualBounds);
            if (!rect.HasArea()) return null;
            var image = ScreenCapture.Crop(screen, new Int32Rect(rect.X - virtualBounds.X, rect.Y - virtualBounds.Y, rect.Width, rect.Height));
            double scale = ScreenInfo.MonitorForRect(monitors, rect).DpiScale;
            string source = selection.Window != null
                ? (string.IsNullOrWhiteSpace(selection.Window.Title) ? "Window" : selection.Window.Title)
                : selection.Monitor != null
                    ? (monitors.Count > 1 ? $"Display {selection.Monitor.Index}" : "Full screen")
                    : $"Region {rect.Width}×{rect.Height}";
            return new CaptureResult(image, scale, source);
        }
        finally
        {
            foreach (var o in overlays) o.Close();
            RestoreWindows(hidden);
            _busy = false;
        }
    }

    private static async Task<List<Window>> HideAppWindowsAsync()
    {
        var visible = Application.Current.Windows.OfType<Window>()
            .Where(w => w.IsVisible && w is not CaptureOverlayWindow && w.WindowState != WindowState.Minimized)
            .ToList();
        foreach (var w in visible) w.Hide();
        if (visible.Count > 0)
        {
            await Task.Delay(180); // let DWM finish removing our windows from the composed desktop
            Native.DwmFlush();
        }
        return visible;
    }

    private static void RestoreWindows(List<Window> windows)
    {
        foreach (var w in windows) w.Show();
    }
}
