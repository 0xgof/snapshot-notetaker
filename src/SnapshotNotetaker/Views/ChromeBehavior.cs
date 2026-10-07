using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SnapshotNotetaker.Interop;

namespace SnapshotNotetaker.Views;

/// <summary>
/// Support for the app's own themed title bar (Themes/Controls.xaml, style "ChromeWindow"):
/// wires the caption buttons and keeps a maximized window inside the monitor's work area.
/// </summary>
public static class ChromeBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ChromeBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>Secondary text shown after the app name in the title bar (e.g. the open snapshot).</summary>
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.RegisterAttached(
        "Caption", typeof(string), typeof(ChromeBehavior), new FrameworkPropertyMetadata(""));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool value) => o.SetValue(IsEnabledProperty, value);
    public static string GetCaption(DependencyObject o) => (string)o.GetValue(CaptionProperty);
    public static void SetCaption(DependencyObject o, string value) => o.SetValue(CaptionProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true) return;

        window.CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(window)));

        if (window.IsInitialized && new WindowInteropHelper(window).Handle != IntPtr.Zero) Hook(window);
        else window.SourceInitialized += (_, _) => Hook(window);
    }

    private static void Hook(Window window)
        => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(WndProc);

    /// <summary>
    /// Without the native frame, a maximized window would hang over the screen edges and cover the taskbar.
    /// Clamp it to the work area of the monitor it is on (physical pixels, so it is right at any DPI).
    /// </summary>
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>(), szDevice = "" };
        if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Width;
        mmi.ptMaxSize.Y = info.rcWork.Height;
        Marshal.StructureToPtr(mmi, lParam, false);
        return IntPtr.Zero; // let WPF still apply MinWidth/MinHeight
    }
}
