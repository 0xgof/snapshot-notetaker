using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapshotNotetaker.Interop;

namespace SnapshotNotetaker.Capture;

/// <summary>GDI-based capture into 32-bit DIB sections, copied straight into frozen WPF bitmaps.</summary>
internal static class ScreenCapture
{
    public static BitmapSource CaptureScreen(Int32Rect area)
        => Blit(area.Width, area.Height, memDc =>
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            try
            {
                return Native.BitBlt(memDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, Native.SRCCOPY | Native.CAPTUREBLT);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        });

    /// <summary>Captures a window even if it is covered by others (DWM redirection via PrintWindow).</summary>
    public static BitmapSource? CaptureWindow(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var windowRect) || windowRect.Width <= 0 || windowRect.Height <= 0) return null;
        var full = Blit(windowRect.Width, windowRect.Height, memDc => Native.PrintWindow(hwnd, memDc, Native.PW_RENDERFULLCONTENT));

        // Trim the invisible resize borders Windows 10/11 adds around top-level windows.
        var frame = WindowFinder.GetFrameBounds(hwnd);
        var crop = new Int32Rect(frame.X - windowRect.Left, frame.Y - windowRect.Top, frame.Width, frame.Height)
            .Intersect(new Int32Rect(0, 0, full.PixelWidth, full.PixelHeight));
        return crop.HasArea() && (crop.Width != full.PixelWidth || crop.Height != full.PixelHeight) ? Crop(full, crop) : full;
    }

    public static BitmapSource Crop(BitmapSource source, Int32Rect area)
    {
        int stride = area.Width * 4;
        var buffer = new byte[stride * area.Height];
        source.CopyPixels(area, buffer, stride, 0);
        var result = BitmapSource.Create(area.Width, area.Height, 96, 96, source.Format, null, buffer, stride);
        result.Freeze();
        return result;
    }

    private static BitmapSource Blit(int width, int height, Func<IntPtr, bool> draw)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Capture area is empty.");

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr bitmap = Native.CreateDIBSection(screenDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate the capture bitmap.");
        }

        IntPtr previous = Native.SelectObject(memDc, bitmap);
        try
        {
            draw(memDc);
            Native.GdiFlush();
            // Bgr32 ignores the undefined alpha byte GDI leaves behind.
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bits, width * height * 4, width * 4);
            result.Freeze();
            return result;
        }
        finally
        {
            Native.SelectObject(memDc, previous);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
