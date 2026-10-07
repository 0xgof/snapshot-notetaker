using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapshotNotetaker.Capture;
using SnapshotNotetaker.IO;
using SnapshotNotetaker.Model;
using SnapshotNotetaker.Settings;

namespace SnapshotNotetaker;

/// <summary>
/// Developer switches for checking rendering and capture without clicking through the UI:
///   --render-demo &lt;dir&gt;                 render the demo snapshot (export with notes right/below + thumbnail)
///   --ui-snapshot &lt;file.png&gt; [theme] [--expand] [--narrow]   open the UI on a throw-away demo library and save a picture of the window
///   --capture-test &lt;dir&gt;                capture every display and write image sizes / brightness to a report
/// None of them touch the user's settings or library.
/// </summary>
internal static class DevTools
{
    public static bool TryRun(App app, string[] args)
    {
        if (args.Length < 2 || !args[0].StartsWith("--") || args[0] == "--tray") return false;

        // Diagnostics runs must not touch the user's log or crash folders either.
        string sandbox = Path.Combine(Path.GetTempPath(), "SnapshotNotetaker-devtools");
        Support.Log.Configure(Path.Combine(sandbox, "logs"));
        Support.CrashHandler.Configure(Path.Combine(sandbox, "crashes"));
        switch (args[0])
        {
            case "--render-demo":
                Run(app, () => RenderDemo(args[1]));
                return true;
            case "--capture-test":
                Run(app, () => CaptureTest(args[1]));
                return true;
            case "--ui-snapshot":
                _ = UiSnapshotAsync(app, args[1], args.Length > 2 ? args[2] : "Light", args.Contains("--expand"), args.Contains("--narrow"));
                return true;
            case "--overlay-test":
                _ = OverlayTestAsync(app, args[1]);
                return true;
            case "--settings-snapshot":
                _ = DialogSnapshotAsync(app, "settings", args[1], args.Length > 2 ? args[2] : "Light");
                return true;
            case "--dialog-snapshot": // --dialog-snapshot <settings|support|problem> <file.png> [theme]
                _ = DialogSnapshotAsync(app, args[1], args.Length > 2 ? args[2] : "out.png", args.Length > 3 ? args[3] : "Light");
                return true;
            default:
                return false;
        }
    }

    private static void Run(App app, Action action)
    {
        try { action(); }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "snapshot-notetaker-devtools.txt"), ex.ToString()); }
        app.Shutdown();
    }

    private static void RenderDemo(string dir)
    {
        Directory.CreateDirectory(dir);
        var doc = DemoContent.CreateDocument("demo");
        Exporter.SaveImage(Exporter.Render(doc, new ExportOptions(NotesLayout.Margins)), Path.Combine(dir, "export-notes-margins.png"));
        Exporter.SaveImage(Exporter.Render(doc, new ExportOptions(NotesLayout.Right)), Path.Combine(dir, "export-notes-right.png"));
        Exporter.SaveImage(Exporter.Render(doc, new ExportOptions(NotesLayout.Bottom)), Path.Combine(dir, "export-notes-below.png"));
        Exporter.SaveImage(Exporter.Render(doc, new ExportOptions(NotesLayout.None)), Path.Combine(dir, "export-image-only.png"));
        Exporter.SaveImage(Exporter.RenderThumbnail(doc, null, out _), Path.Combine(dir, "thumbnail.png"));
        File.WriteAllText(Path.Combine(dir, "notes.md"), Exporter.NotesAsMarkdown(doc));

        // Round-trip through the exchange format.
        string snap = Path.Combine(dir, "demo" + ProjectFile.Extension);
        ProjectFile.Write(doc, snap);
        var (image, file) = ProjectFile.Read(snap);
        File.WriteAllText(Path.Combine(dir, "roundtrip.txt"),
            $"image {image.PixelWidth}x{image.PixelHeight}, annotations {file.Annotations.Count}, numbering {file.Numbering}");
    }

    private static void CaptureTest(string dir)
    {
        Directory.CreateDirectory(dir);
        var report = new List<string>();
        var monitors = ScreenInfo.GetMonitors();
        var virtualBounds = ScreenInfo.VirtualBounds();
        report.Add($"virtual {virtualBounds.X},{virtualBounds.Y} {virtualBounds.Width}x{virtualBounds.Height}");
        foreach (var m in monitors)
        {
            var bitmap = ScreenCapture.CaptureScreen(m.Bounds);
            report.Add($"{m.Description} at {m.Bounds.X},{m.Bounds.Y}: captured {bitmap.PixelWidth}x{bitmap.PixelHeight}, mean luminance {MeanLuminance(bitmap):0.000}");
        }
        var windows = WindowFinder.GetWindows();
        report.Add($"{windows.Count} visible windows, {windows.Count(w => w.IsAppWindow)} app windows");
        var target = windows.FirstOrDefault(w => w.IsAppWindow);
        if (target != null)
        {
            var shot = ScreenCapture.CaptureWindow(target.Handle);
            report.Add(shot == null ? "PrintWindow failed" : $"PrintWindow of an app window: {shot.PixelWidth}x{shot.PixelHeight}, mean luminance {MeanLuminance(shot):0.000} (frame {target.Bounds.Width}x{target.Bounds.Height})");
        }
        File.WriteAllLines(Path.Combine(dir, "capture-report.txt"), report);
    }

    /// <summary>Opens the capture overlays briefly and checks each one exactly covers its monitor (mixed-DPI setups).</summary>
    private static async Task OverlayTestAsync(App app, string dir)
    {
        var report = new List<string>();
        try
        {
            Directory.CreateDirectory(dir);
            var monitors = ScreenInfo.GetMonitors();
            var virtualBounds = ScreenInfo.VirtualBounds();
            var session = new CaptureSession(ScreenCapture.CaptureScreen(virtualBounds), virtualBounds, monitors, WindowFinder.GetWindows(), CaptureMode.Region);
            var overlays = monitors.Select(m => new CaptureOverlayWindow(session, m)).ToList();
            foreach (var o in overlays) o.Show();
            await Task.Delay(700);
            foreach (var o in overlays)
            {
                Interop.Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(o).Handle, out var r);
                var dpi = VisualTreeHelper.GetDpi(o);
                var content = (FrameworkElement)o.Content;
                var b = o.Monitor.Bounds;
                bool ok = r.Left == b.X && r.Top == b.Y && r.Width == b.Width && r.Height == b.Height
                          && Math.Abs(content.ActualWidth * dpi.DpiScaleX - b.Width) < 1.5 && Math.Abs(content.ActualHeight * dpi.DpiScaleY - b.Height) < 1.5;
                report.Add($"{(ok ? "OK  " : "FAIL")} {o.Monitor.Description}: monitor {b.X},{b.Y} {b.Width}x{b.Height} | window {r.Left},{r.Top} {r.Width}x{r.Height} | " +
                           $"WPF dpi {dpi.DpiScaleX:0.##}, content {content.ActualWidth * dpi.DpiScaleX:0}x{content.ActualHeight * dpi.DpiScaleY:0} px");
            }
            foreach (var o in overlays) o.Close();
        }
        catch (Exception ex)
        {
            report.Add(ex.ToString());
        }
        File.WriteAllLines(Path.Combine(dir, "overlay-report.txt"), report);
        app.Shutdown();
    }

    private static async Task DialogSnapshotAsync(App app, string which, string output, string theme)
    {
        try
        {
            app.StartSandbox(new AppSettings { Theme = theme });
            Window window = which switch
            {
                "support" => new Views.SupportWindow(app),
                "problem" => Views.ProblemWindow.CreateForPreview(new InvalidOperationException("Sample error used to preview this dialog.")),
                _ => new Views.SettingsWindow(app),
            };
            window.Show();
            await Task.Delay(600);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveWindowPicture(window, output);
            window.Close();
        }
        catch (Exception ex)
        {
            File.WriteAllText(output + ".error.txt", ex.ToString());
        }
        app.Shutdown();
    }

    /// <summary>Renders a window's client area (background + content) to a PNG at device resolution.</summary>
    private static void SaveWindowPicture(Window window, string output)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var size = new Size(window.ActualWidth, window.ActualHeight); // whole window incl. the custom title bar
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(window), null, new Rect(size));
        }
        var bitmap = new RenderTargetBitmap((int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Exporter.SaveImage(bitmap, output);
    }

    private static double MeanLuminance(BitmapSource bitmap)
        => new Rendering.ImageSampler(bitmap).Luminance(new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight)) ?? 0;

    private static async Task UiSnapshotAsync(App app, string output, string theme, bool expand, bool narrow)
    {
        try
        {
            string libraryDir = Path.Combine(Path.GetTempPath(), "SnapshotNotetaker-ui-" + Guid.NewGuid().ToString("N")[..8]);
            var settings = new AppSettings { Theme = theme, LibraryFolder = libraryDir, OpenLastSnapshotOnStart = false, ExpandWithNotes = expand };
            var window = app.StartSandbox(settings);
            window.Width = narrow ? 1000 : 1440;
            window.Height = 900;

            var times = new[] { DateTime.Now.AddMinutes(-3), DateTime.Now.AddDays(-1), DateTime.Now.AddDays(-4) };
            for (int i = 2; i >= 0; i--)
            {
                var image = DemoContent.CreateScreenshot(variant: i);
                var (_, doc) = app.Library.Create(image, 1, i == 0 ? "Region 1600×1000" : $"Demo window {i}", times[i]);
                doc.Title = i switch { 0 => "Dashboard review", 1 => "Settings page", _ => "Checkout flow" };
                if (i == 0) DemoContent.AddAnnotations(doc);
                app.Library.Save(doc);
            }
            app.Library.Flush();
            await app.Library.LoadAsync();

            window.Show();
            await window.OpenEntryAsync(app.Library.Entries[0]);
            window.DevSelect(2);
            await Task.Delay(900);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveWindowPicture(window, output);
            try { Directory.Delete(libraryDir, true); } catch { /* temp folder */ }
        }
        catch (Exception ex)
        {
            File.WriteAllText(output + ".error.txt", ex.ToString());
        }
        app.Shutdown();
    }
}

/// <summary>A synthetic "screenshot" and a set of annotations that exercise every label style.</summary>
internal static class DemoContent
{
    private static readonly Typeface Regular = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Semibold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static AnnotationDocument CreateDocument(string id)
    {
        var doc = new AnnotationDocument(id, CreateScreenshot(0), 1, "Region 1600×1000", new DateTime(2026, 10, 7, 14, 32, 0), "Dashboard review");
        AddAnnotations(doc);
        return doc;
    }

    public static void AddAnnotations(AnnotationDocument doc)
    {
        Color red = C("#E53935"), blue = C("#1E88E5"), orange = C("#FB8C00"), purple = C("#8E24AA"), teal = C("#00897B"), green = C("#2E7D32");

        doc.Annotations.Add(new Annotation
        {
            Kind = ShapeKind.Rectangle, Bounds = new Rect(560, 150, 300, 132), StrokeColor = red, StrokeWidth = 3,
            Label = new LabelStyle { Shape = LabelShape.Box, Anchor = LabelAnchor.TopLeft, Placement = LabelPlacement.OnEdge, FontSize = 15 },
            Comment = "Active users should include returning customers — the number looks ~20% low.",
        });
        doc.Annotations.Add(new Annotation
        {
            Kind = ShapeKind.Ellipse, Bounds = new Rect(838, 336, 150, 230), StrokeColor = blue, StrokeWidth = 3,
            Label = new LabelStyle { Shape = LabelShape.Circle, Anchor = LabelAnchor.TopRight, Placement = LabelPlacement.Outside, FontSize = 15 },
            LabelMode = LabelMode.NumberAndTag, Tag = "Peak",
            Comment = "August spike: confirm it's the promo campaign and not double-counted orders.",
        });
        doc.Annotations.Add(new Annotation
        {
            Kind = ShapeKind.Spline, IsClosed = true, StrokeColor = orange, StrokeWidth = 3, FillOpacity = 0.12,
            Points = new[] { new Point(1218, 676), new Point(1330, 650), new Point(1392, 700), new Point(1384, 836), new Point(1300, 872), new Point(1214, 820) },
            Label = new LabelStyle { Shape = LabelShape.Callout, Anchor = LabelAnchor.Right, FontSize = 15 },
            Comment = "Status pills: \"Pending\" and \"Failed\" are hard to tell apart for color-blind users.",
        });
        doc.Annotations.Add(new Annotation
        {
            Kind = ShapeKind.Square, Bounds = new Rect(1514, 8, 42, 42), StrokeColor = purple, StrokeWidth = 2.5,
            Label = new LabelStyle { Shape = LabelShape.CornerTab, Anchor = LabelAnchor.BottomRight, Placement = LabelPlacement.Outside, FontSize = 13 },
            LabelMode = LabelMode.NumberAndTag, Tag = "Profile",
            Comment = "Avatar menu has no keyboard focus ring.",
        });
        doc.Annotations.Add(new Annotation
        {
            Kind = ShapeKind.Rectangle, Bounds = new Rect(1150, 904, 420, 66), StrokeColor = teal, StrokeWidth = 3, Dash = StrokeDash.Dashed, StrokeOutline = true,
            Label = new LabelStyle { Shape = LabelShape.Halo, Anchor = LabelAnchor.TopLeft, Placement = LabelPlacement.Outside, FontSize = 22, Scheme = LabelColorScheme.MatchShape },
        });
        doc.Annotations.Add(new Annotation
        {
            Kind = ShapeKind.Circle, Bounds = new Rect(1386, 76, 172, 172), StrokeColor = green, StrokeWidth = 3,
            Label = new LabelStyle { Shape = LabelShape.Box, Anchor = LabelAnchor.BottomLeft, Placement = LabelPlacement.OnEdge, Scheme = LabelColorScheme.Adaptive, FontSize = 15 },
            Comment = "Primary action is outside the visible fold on 1366×768 laptops.",
        });
        doc.IsDirty = false;
    }

    /// <summary>A plausible web-dashboard screenshot (variant changes the accent so library thumbnails differ).</summary>
    public static BitmapSource CreateScreenshot(int variant)
    {
        const int w = 1600, h = 1000;
        var accent = variant switch { 1 => C("#7C3AED"), 2 => C("#059669"), _ => C("#2563EB") };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(B("#F3F5F9"), null, new Rect(0, 0, w, h));

            // Top bar
            dc.DrawRectangle(B("#111827"), null, new Rect(0, 0, w, 58));
            dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, new Rect(22, 16, 26, 26), 6, 6);
            Text(dc, "Acme Analytics", 60, 17, 18, Brushes.White, Semibold);
            double nx = 280;
            foreach (var item in new[] { "Overview", "Reports", "Customers", "Billing", "Settings" })
            {
                Text(dc, item, nx, 20, 14, item == "Overview" ? Brushes.White : B("#9CA3AF"), Semibold);
                nx += 110;
            }
            dc.DrawEllipse(B("#F59E0B"), null, new Point(1535, 29), 16, 16);
            Text(dc, "GO", 1525, 20, 12, B("#111827"), Semibold);

            // Sidebar
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 58, 230, h - 58));
            dc.DrawRectangle(B("#E5E7EB"), null, new Rect(230, 58, 1, h - 58));
            double sy = 92;
            foreach (var item in new[] { "Dashboard", "Revenue", "Orders", "Products", "Campaigns", "Team", "Integrations" })
            {
                if (item == "Dashboard") dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B)), null, new Rect(14, sy - 8, 202, 36), 8, 8);
                dc.DrawRoundedRectangle(B("#D1D5DB"), null, new Rect(30, sy + 2, 16, 16), 4, 4);
                Text(dc, item, 58, sy, 14, item == "Dashboard" ? new SolidColorBrush(accent) : B("#374151"), Semibold);
                sy += 48;
            }

            // Heading + actions
            Text(dc, variant switch { 1 => "Account settings", 2 => "Checkout", _ => "Revenue overview" }, 262, 84, 26, B("#111827"), Semibold);
            Text(dc, "Last 12 months · updated 5 minutes ago", 264, 122, 13.5, B("#6B7280"), Regular);
            dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, new Rect(1400, 136, 140, 40), 8, 8);
            Text(dc, "Export report", 1424, 146, 14, Brushes.White, Semibold);

            // KPI cards
            string[] labels = { "Revenue", "Active users", "Conversion" };
            string[] values = { "$48,920", "1,284", "3.2%" };
            string[] deltas = { "+12.4%", "−3.1%", "+0.4 pt" };
            for (int i = 0; i < 3; i++)
            {
                var r = new Rect(262 + i * 300, 160, 280, 116);
                Card(dc, r);
                Text(dc, labels[i], r.X + 20, r.Y + 18, 13.5, B("#6B7280"), Semibold);
                Text(dc, values[i], r.X + 20, r.Y + 42, 32, B("#111827"), Semibold);
                Text(dc, deltas[i], r.X + 20, r.Y + 86, 13, deltas[i].StartsWith("−") ? B("#DC2626") : B("#16A34A"), Semibold);
            }

            // Chart
            var chart = new Rect(262, 300, 880, 330);
            Card(dc, chart);
            Text(dc, "Monthly revenue", chart.X + 20, chart.Y + 16, 15, B("#111827"), Semibold);
            double[] bars = { 0.42, 0.48, 0.45, 0.55, 0.6, 0.58, 0.66, 0.95, 0.7, 0.68, 0.74, 0.8 };
            string[] months = { "Nov", "Dec", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct" };
            for (int i = 0; i < bars.Length; i++)
            {
                double bh = bars[i] * 220, bx = chart.X + 40 + i * 68;
                dc.DrawRectangle(B("#EEF2F7"), null, new Rect(chart.X + 20, chart.Y + 70 + i * 0, 1, 0));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(i == 9 ? 255 : 190), accent.R, accent.G, accent.B)), null, new Rect(bx, chart.Bottom - 40 - bh, 40, bh), 4, 4);
                Text(dc, months[i], bx + 6, chart.Bottom - 32, 12, B("#6B7280"), Regular);
            }

            // Side card
            var side = new Rect(1162, 300, 410, 330);
            Card(dc, side);
            Text(dc, "Top channels", side.X + 20, side.Y + 16, 15, B("#111827"), Semibold);
            string[] channels = { "Organic search", "Email", "Paid social", "Referral", "Direct" };
            double[] shares = { 0.82, 0.64, 0.47, 0.3, 0.22 };
            for (int i = 0; i < channels.Length; i++)
            {
                double y = side.Y + 62 + i * 50;
                Text(dc, channels[i], side.X + 20, y, 13.5, B("#374151"), Regular);
                dc.DrawRoundedRectangle(B("#EEF2F7"), null, new Rect(side.X + 20, y + 24, 370, 8), 4, 4);
                dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, new Rect(side.X + 20, y + 24, 370 * shares[i], 8), 4, 4);
            }

            // Orders table
            var table = new Rect(262, 650, 1310, 236);
            Card(dc, table);
            Text(dc, "Recent orders", table.X + 20, table.Y + 16, 15, B("#111827"), Semibold);
            string[] cols = { "Order", "Customer", "Date", "Amount", "Status" };
            double[] colX = { 20, 200, 560, 800, 960 };
            for (int c = 0; c < cols.Length; c++) Text(dc, cols[c].ToUpperInvariant(), table.X + colX[c], table.Y + 54, 11.5, B("#6B7280"), Semibold);
            string[,] rows =
            {
                { "#10482", "Maria López", "Oct 6, 2026", "$1,240.00", "Paid" },
                { "#10481", "Jonas Berg", "Oct 6, 2026", "$86.50", "Pending" },
                { "#10480", "Aiko Tanaka", "Oct 5, 2026", "$312.99", "Failed" },
                { "#10479", "Samuel Okafor", "Oct 5, 2026", "$54.00", "Paid" },
            };
            for (int r = 0; r < rows.GetLength(0); r++)
            {
                double y = table.Y + 84 + r * 36;
                dc.DrawRectangle(B("#EEF1F5"), null, new Rect(table.X + 16, y - 8, table.Width - 32, 1));
                for (int c = 0; c < 4; c++) Text(dc, rows[r, c], table.X + colX[c], y, 13.5, B("#1F2937"), Regular);
                string status = rows[r, 4];
                var (bg, fg) = status switch { "Paid" => ("#DCFCE7", "#166534"), "Pending" => ("#FEF3C7", "#92400E"), _ => ("#FEE2E2", "#991B1B") };
                dc.DrawRoundedRectangle(B(bg), null, new Rect(table.X + colX[4] - 6, y - 3, 84, 24), 12, 12);
                Text(dc, status, table.X + colX[4] + 6, y, 12.5, B(fg), Semibold);
            }

            // Toast
            var toast = new Rect(1160, 912, 400, 50);
            dc.DrawRoundedRectangle(B("#1F2937"), null, toast, 8, 8);
            dc.DrawEllipse(B("#EF4444"), null, new Point(toast.X + 24, toast.Y + 25), 7, 7);
            Text(dc, "Payment sync failed — retrying in 30 s", toast.X + 42, toast.Y + 15, 13.5, Brushes.White, Semibold);
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return AnnotationDocument.NormalizeImage(new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0));
    }

    private static void Card(DrawingContext dc, Rect r)
    {
        var shadow = r;
        shadow.Offset(0, 2);
        dc.DrawRoundedRectangle(B("#E4E7EC"), null, shadow, 10, 10);
        dc.DrawRoundedRectangle(Brushes.White, null, r, 10, 10);
    }

    private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush, Typeface face)
        => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, 1.0), new Point(x, y));

    private static SolidColorBrush B(string hex) => new(C(hex));

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
