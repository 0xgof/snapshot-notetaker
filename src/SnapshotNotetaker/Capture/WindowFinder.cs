using System.Runtime.InteropServices;
using System.Windows;
using SnapshotNotetaker.Interop;

namespace SnapshotNotetaker.Capture;

public sealed record WindowInfo(IntPtr Handle, string Title, string ClassName, Int32Rect Bounds, bool IsAppWindow);

internal static class WindowFinder
{
    /// <summary>Visible top-level windows of other processes, in z-order (topmost first).</summary>
    public static List<WindowInfo> GetWindows()
    {
        var result = new List<WindowInfo>();
        uint ownPid = (uint)Environment.ProcessId;
        IntPtr shell = Native.GetShellWindow();

        Native.EnumWindowsProc proc = (hwnd, _) =>
        {
            if (hwnd == shell || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid || IsCloaked(hwnd)) return true;

            long exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            if ((exStyle & Native.WS_EX_TRANSPARENT) != 0 && (exStyle & Native.WS_EX_LAYERED) != 0) return true; // click-through overlays

            string className = Native.GetWindowClass(hwnd);
            if (className is "Progman" or "WorkerW") return true; // the desktop

            var bounds = GetFrameBounds(hwnd);
            if (bounds.Width < 8 || bounds.Height < 8) return true;

            string title = Native.GetWindowTitle(hwnd);
            bool owned = Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero;
            bool isApp = title.Length > 0 && (exStyle & Native.WS_EX_TOOLWINDOW) == 0 && (!owned || (exStyle & Native.WS_EX_APPWINDOW) != 0);
            result.Add(new WindowInfo(hwnd, title, className, bounds, isApp));
            return true;
        };
        Native.EnumWindows(proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return result;
    }

    /// <summary>Visible frame bounds (excludes the invisible resize borders).</summary>
    public static Int32Rect GetFrameBounds(IntPtr hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) == 0 && r.Width > 0)
            return r.ToInt32Rect();
        return Native.GetWindowRect(hwnd, out r) ? r.ToInt32Rect() : Int32Rect.Empty;
    }

    private static bool IsCloaked(IntPtr hwnd)
        => Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
}
