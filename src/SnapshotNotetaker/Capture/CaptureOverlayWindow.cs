using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapshotNotetaker.Interop;
using SnapshotNotetaker.Rendering;

namespace SnapshotNotetaker.Capture;

/// <summary>
/// Full-screen, frozen-screenshot overlay for one monitor. One window per monitor keeps every overlay at its
/// monitor's native DPI; all of them share a <see cref="CaptureSession"/> and draw the selection in global pixels,
/// so a region can still span monitors.
/// </summary>
internal sealed class CaptureOverlayWindow : Window
{
    private readonly CaptureSession _session;
    private readonly OverlayLayer _layer;
    private IntPtr _hwnd;

    public CaptureOverlayWindow(CaptureSession session, MonitorInfo monitor)
    {
        _session = session;
        Monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Title = "Snapshot capture";

        // Approximate placement; the exact physical rect is applied once the HWND exists.
        Left = monitor.Bounds.X / monitor.DpiScale;
        Top = monitor.Bounds.Y / monitor.DpiScale;
        Width = monitor.Bounds.Width / monitor.DpiScale;
        Height = monitor.Bounds.Height / monitor.DpiScale;

        var crop = new CroppedBitmap(session.Screen, new Int32Rect(
            monitor.Bounds.X - session.Virtual.X, monitor.Bounds.Y - session.Virtual.Y, monitor.Bounds.Width, monitor.Bounds.Height));
        crop.Freeze();
        var image = new Image { Source = crop, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        _layer = new OverlayLayer(session, monitor);
        Content = new Grid { Children = { image, _layer } };

        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ApplyBounds);
        Loaded += (_, _) => ApplyBounds();
        session.Changed += _layer.InvalidateVisual;
        Closed += (_, _) => session.Changed -= _layer.InvalidateVisual;
    }

    public MonitorInfo Monitor { get; }

    public void ActivateForInput()
    {
        Activate();
        if (_hwnd != IntPtr.Zero) Native.SetForegroundWindow(_hwnd);
        Focus();
        Keyboard.Focus(this);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyBounds();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_DPICHANGED)
        {
            // Force the suggested rect to be exactly our monitor, whatever DPI we land on.
            var b = Monitor.Bounds;
            Marshal.StructureToPtr(new Native.RECT { Left = b.X, Top = b.Y, Right = b.X + b.Width, Bottom = b.Y + b.Height }, lParam, false);
        }
        return IntPtr.Zero;
    }

    private void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero) return;
        var b = Monitor.Bounds;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_NOACTIVATE);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        _session.UpdateCursor();
        _session.BeginDrag();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _session.UpdateCursor();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        _session.UpdateCursor();
        if (_session.Dragging) _session.EndDrag();
        else _session.CompleteHover();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_session.Dragging)
        {
            ReleaseMouseCapture();
            _session.CancelDrag();
        }
        else _session.Cancel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        switch (e.Key)
        {
            case Key.Escape: _session.Cancel(); break;
            case Key.Enter or Key.Space when !_session.Dragging: _session.CompleteHover(); break;
            case Key.Left: _session.MoveCursorBy(-step, 0); break;
            case Key.Right: _session.MoveCursorBy(step, 0); break;
            case Key.Up: _session.MoveCursorBy(0, -step); break;
            case Key.Down: _session.MoveCursorBy(0, step); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Dimming, selection frame, crosshair, magnifier and hints for one monitor.</summary>
    private sealed class OverlayLayer : FrameworkElement
    {
        private static readonly Brush Dim = Frozen(Color.FromArgb(0x78, 0, 0, 0));
        private static readonly Brush Accent = Frozen(Color.FromRgb(0x3B, 0x82, 0xF6));
        private static readonly Brush PillBrush = Frozen(Color.FromArgb(0xE6, 0x1F, 0x23, 0x28));
        private static readonly Pen AccentPen = FrozenPen(Accent, 2);
        private static readonly Pen WhitePen = FrozenPen(Brushes.White, 1);
        private static readonly Pen CrosshairPen = FrozenPen(Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1);
        private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        private readonly CaptureSession _session;
        private readonly MonitorInfo _monitor;

        public OverlayLayer(CaptureSession session, MonitorInfo monitor)
        {
            _session = session;
            _monitor = monitor;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }

        private double ScaleX => ActualWidth / _monitor.Bounds.Width;
        private double ScaleY => ActualHeight / _monitor.Bounds.Height;

        private Rect ToLocal(Int32Rect r)
            => new((r.X - _monitor.Bounds.X) * ScaleX, (r.Y - _monitor.Bounds.Y) * ScaleY, r.Width * ScaleX, r.Height * ScaleY);

        private Point ToLocal(int x, int y)
            => new((x - _monitor.Bounds.X + 0.5) * ScaleX, (y - _monitor.Bounds.Y + 0.5) * ScaleY);

        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth <= 0) return;
            var full = new Rect(0, 0, ActualWidth, ActualHeight);
            Int32Rect? focus = _session.Dragging ? _session.Selection : _session.Hover;

            if (focus is { } f && f.HasArea())
            {
                var local = ToLocal(f);
                var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
                dim.Children.Add(new RectangleGeometry(full));
                dim.Children.Add(new RectangleGeometry(local));
                dc.DrawGeometry(Dim, null, dim);
                dc.DrawRectangle(null, AccentPen, local);
                DrawSizeLabel(dc, local, f);
            }
            else dc.DrawRectangle(Dim, null, full);

            bool cursorHere = _monitor.Bounds.Contains(_session.CursorX, _session.CursorY);
            var cursor = ToLocal(_session.CursorX, _session.CursorY);
            if (_session.Mode == CaptureMode.Region)
            {
                dc.DrawLine(CrosshairPen, new Point(0, cursor.Y), new Point(ActualWidth, cursor.Y));
                dc.DrawLine(CrosshairPen, new Point(cursor.X, 0), new Point(cursor.X, ActualHeight));
            }

            if (cursorHere)
            {
                DrawHint(dc);
                if (_session.Mode == CaptureMode.Region) DrawMagnifier(dc, cursor);
            }
        }

        private void DrawSizeLabel(DrawingContext dc, Rect local, Int32Rect r)
        {
            string text = _session.HoverWindow != null && !_session.Dragging && !string.IsNullOrEmpty(_session.HoverWindow.Title)
                ? $"{Truncate(_session.HoverWindow.Title, 60)}  ·  {r.Width} × {r.Height}"
                : $"{r.Width} × {r.Height}";
            var ft = Text(text, 12, Brushes.White);
            double x = Math.Clamp(local.X, 4, Math.Max(4, ActualWidth - ft.Width - 20));
            double y = local.Y - ft.Height - 12 >= 0 ? local.Y - ft.Height - 12 : local.Y + 6;
            var pill = new Rect(x, y, ft.Width + 16, ft.Height + 6);
            dc.DrawRoundedRectangle(PillBrush, null, pill, 4, 4);
            dc.DrawText(ft, new Point(pill.X + 8, pill.Y + 3));
        }

        private void DrawHint(DrawingContext dc)
        {
            string hint = _session.Mode == CaptureMode.Region
                ? "Drag to select an area  ·  Click to capture the highlighted window  ·  Arrows nudge  ·  Esc cancels"
                : "Click a window to capture it  ·  Esc cancels";
            var ft = Text(hint, 13, Brushes.White);
            var pill = new Rect((ActualWidth - ft.Width) / 2 - 14, 18, ft.Width + 28, ft.Height + 12);
            dc.DrawRoundedRectangle(PillBrush, null, pill, pill.Height / 2, pill.Height / 2);
            dc.DrawText(ft, new Point(pill.X + 14, pill.Y + 6));
        }

        private void DrawMagnifier(DrawingContext dc, Point cursor)
        {
            const int cells = 15;
            const double size = 135;
            double x = cursor.X + 24, y = cursor.Y + 24;
            if (x + size > ActualWidth - 4) x = cursor.X - 24 - size;
            if (y + size + 30 > ActualHeight - 4) y = cursor.Y - 24 - size - 30;
            var box = new Rect(x, y, size, size);

            int px = _session.CursorX - _session.Virtual.X, py = _session.CursorY - _session.Virtual.Y;
            var brush = new ImageBrush(_session.Screen)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(px - cells / 2, py - cells / 2, cells, cells),
                Stretch = Stretch.Fill,
            };
            dc.DrawRectangle(Brushes.Black, null, box);
            dc.DrawRectangle(brush, null, box);
            double cell = size / cells;
            var center = new Rect(x + cells / 2 * cell, y + cells / 2 * cell, cell, cell);
            dc.DrawRectangle(null, FrozenPen(Accent, 1.5), center);
            dc.DrawRectangle(null, WhitePen, box);

            var color = PixelAt(px, py);
            string info = $"{_session.CursorX}, {_session.CursorY}   {color.R:X2}{color.G:X2}{color.B:X2}";
            var ft = Text(info, 11.5, Brushes.White);
            var pill = new Rect(x, y + size + 4, size, ft.Height + 8);
            dc.DrawRoundedRectangle(PillBrush, null, pill, 4, 4);
            dc.DrawRectangle(ColorUtil.Brush(color), WhitePen, new Rect(pill.X + 6, pill.Y + (pill.Height - 10) / 2, 10, 10));
            dc.DrawText(ft, new Point(pill.X + 22, pill.Y + 4));
        }

        private Color PixelAt(int x, int y)
        {
            if (x < 0 || y < 0 || x >= _session.Screen.PixelWidth || y >= _session.Screen.PixelHeight) return Colors.Black;
            var px = new byte[4];
            _session.Screen.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
            return Color.FromRgb(px[2], px[1], px[0]);
        }

        private FormattedText Text(string text, double size, Brush brush)
            => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Pen FrozenPen(Brush brush, double width)
        {
            var p = new Pen(brush, width);
            p.Freeze();
            return p;
        }
    }
}
