using System.Runtime.InteropServices;
using System.Windows;
using SnapshotNotetaker.Interop;

namespace SnapshotNotetaker.Capture;

public sealed record MonitorInfo(int Index, Int32Rect Bounds, Int32Rect WorkArea, double DpiScale, bool IsPrimary, string DeviceName)
{
    public string Description => $"Display {Index} ({Bounds.Width}×{Bounds.Height}, {DpiScale * 100:0}%)";
}

internal static class ScreenInfo
{
    public static Int32Rect VirtualBounds() => new(
        Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        Native.MonitorEnumProc proc = (IntPtr handle, IntPtr hdc, ref Native.RECT rect, IntPtr data) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>(), szDevice = "" };
            if (Native.GetMonitorInfo(handle, ref info))
            {
                double scale = 1;
                if (Native.GetDpiForMonitor(handle, Native.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0) scale = dpiX / 96.0;
                list.Add(new MonitorInfo(list.Count + 1, info.rcMonitor.ToInt32Rect(), info.rcWork.ToInt32Rect(), scale,
                    (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0, info.szDevice ?? ""));
            }
            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        // Number displays left-to-right, top-to-bottom like Windows' display settings roughly does.
        var ordered = list.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).Select((m, i) => m with { Index = i + 1 }).ToList();
        return ordered;
    }

    public static (int X, int Y) CursorPosition()
    {
        Native.GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    public static MonitorInfo MonitorAt(IReadOnlyList<MonitorInfo> monitors, int x, int y)
    {
        foreach (var m in monitors)
            if (m.Bounds.Contains(x, y)) return m;
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public static MonitorInfo MonitorForRect(IReadOnlyList<MonitorInfo> monitors, Int32Rect r)
        => MonitorAt(monitors, r.X + r.Width / 2, r.Y + r.Height / 2);
}

internal static class Int32RectExtensions
{
    public static bool Contains(this Int32Rect r, int x, int y)
        => x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    public static Int32Rect Intersect(this Int32Rect a, Int32Rect b)
    {
        int x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X + a.Width, b.X + b.Width), y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return x2 <= x1 || y2 <= y1 ? Int32Rect.Empty : new Int32Rect(x1, y1, x2 - x1, y2 - y1);
    }

    public static bool HasArea(this Int32Rect r) => r.Width > 0 && r.Height > 0;
}
