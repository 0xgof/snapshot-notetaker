using System.Windows;
using System.Windows.Media.Imaging;
using SnapshotNotetaker.Interop;

namespace SnapshotNotetaker.Capture;

internal enum CaptureMode { Region, Window }

internal sealed record CaptureSelection(Int32Rect Rect, WindowInfo? Window, MonitorInfo? Monitor);

/// <summary>Shared state of one interactive capture across all per-monitor overlay windows (physical pixels).</summary>
internal sealed class CaptureSession
{
    private readonly TaskCompletionSource<CaptureSelection?> _result = new();

    public CaptureSession(BitmapSource screen, Int32Rect virtualBounds, IReadOnlyList<MonitorInfo> monitors,
                          IReadOnlyList<WindowInfo> windows, CaptureMode mode)
    {
        Screen = screen;
        Virtual = virtualBounds;
        Monitors = monitors;
        Windows = windows;
        Mode = mode;
        UpdateCursor();
    }

    public BitmapSource Screen { get; }
    public Int32Rect Virtual { get; }
    public IReadOnlyList<MonitorInfo> Monitors { get; }
    public IReadOnlyList<WindowInfo> Windows { get; }
    public CaptureMode Mode { get; }

    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool Dragging { get; private set; }
    public int StartX { get; private set; }
    public int StartY { get; private set; }
    public Int32Rect? Hover { get; private set; }
    public WindowInfo? HoverWindow { get; private set; }
    public MonitorInfo? HoverMonitor { get; private set; }

    public Task<CaptureSelection?> Result => _result.Task;

    public event Action? Changed;

    public Int32Rect Selection
    {
        get
        {
            int x1 = Math.Min(StartX, CursorX), y1 = Math.Min(StartY, CursorY);
            int x2 = Math.Max(StartX, CursorX), y2 = Math.Max(StartY, CursorY);
            return new Int32Rect(x1, y1, x2 - x1, y2 - y1).Intersect(Virtual);
        }
    }

    public void UpdateCursor()
    {
        Native.GetCursorPos(out var p);
        CursorX = Math.Clamp(p.X, Virtual.X, Virtual.X + Virtual.Width - 1);
        CursorY = Math.Clamp(p.Y, Virtual.Y, Virtual.Y + Virtual.Height - 1);
        if (!Dragging) UpdateHover();
        Changed?.Invoke();
    }

    public void MoveCursorBy(int dx, int dy)
    {
        Native.SetCursorPos(CursorX + dx, CursorY + dy);
        UpdateCursor();
    }

    public void BeginDrag()
    {
        if (Mode != CaptureMode.Region) return;
        Dragging = true;
        StartX = CursorX;
        StartY = CursorY;
        Changed?.Invoke();
    }

    public void EndDrag()
    {
        if (!Dragging) return;
        Dragging = false;
        var selection = Selection;
        if (selection.Width >= 4 && selection.Height >= 4) Complete(new CaptureSelection(selection, null, null));
        else
        {
            UpdateHover();
            CompleteHover(); // a click (no drag) captures the window/monitor under the cursor
        }
    }

    public void CancelDrag()
    {
        Dragging = false;
        UpdateHover();
        Changed?.Invoke();
    }

    public void CompleteHover()
    {
        if (Hover is { } rect && rect.HasArea()) Complete(new CaptureSelection(rect, HoverWindow, HoverMonitor));
    }

    public void Cancel() => Complete(null);

    private void Complete(CaptureSelection? selection) => _result.TrySetResult(selection);

    private void UpdateHover()
    {
        HoverWindow = Windows.FirstOrDefault(w => w.Bounds.Contains(CursorX, CursorY));
        if (HoverWindow != null)
        {
            Hover = HoverWindow.Bounds.Intersect(Virtual);
            HoverMonitor = null;
        }
        else
        {
            HoverMonitor = ScreenInfo.MonitorAt(Monitors, CursorX, CursorY);
            Hover = HoverMonitor.Bounds;
        }
    }
}
