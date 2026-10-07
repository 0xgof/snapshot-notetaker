using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SnapshotNotetaker.Model;

namespace SnapshotNotetaker.Rendering;

public readonly record struct LabelColors(Color Fill, Color Text, Color Border);

/// <summary>Resolved geometry and colors of one label, in image coordinates.</summary>
public sealed class LabelLayout
{
    public required Rect Box { get; init; }
    /// <summary>Body outline (badge, tab, balloon incl. tail). Null for halo text.</summary>
    public Geometry? Body { get; init; }
    public required FormattedText Text { get; init; }
    public required Point TextOrigin { get; init; }
    public required LabelColors Colors { get; init; }
    public required LabelStyle Style { get; init; }

    public Rect Bounds => Body is null ? Box : Rect.Union(Box, Body.Bounds);
}

public static class LabelRenderer
{
    private static readonly FontFamily Family = new("Segoe UI");
    private static readonly Typeface BoldFace = new(Family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface RegularFace = new(Family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly SolidColorBrush ShadowBrush = CreateFrozen(Color.FromArgb(0x50, 0, 0, 0));

    /// <summary>Lays out the label of an annotation on the image. Returns null when there is nothing to show.</summary>
    /// <param name="sampleLuminance">Average luminance of the image under a rect (for the adaptive scheme).</param>
    public static LabelLayout? Layout(Annotation a, double pixelsPerDip, Func<Rect, double?>? sampleLuminance = null)
    {
        string text = a.DisplayText;
        if (string.IsNullOrEmpty(text)) return null;
        var bounds = a.ShapeBounds;
        if (bounds.IsEmpty) return null;

        var style = a.Label;
        var ft = CreateText(text, style, pixelsPerDip);
        var size = BodySize(ft, style);
        var (dx, dy) = Direction(style.Anchor);
        var anchor = AnchorPoint(a, bounds, dx, dy);
        double gap = a.StrokeWidth / 2 + Math.Max(1, style.FontSize * 0.08);

        Rect box;
        Geometry? body;
        switch (style.Shape)
        {
            case LabelShape.CornerTab:
                box = TabBox(bounds, size, style, dx, dy, a.StrokeWidth);
                box.Offset(a.LabelOffset);
                body = TabGeometry(box, style, dy);
                break;
            case LabelShape.Callout:
                (box, body) = Callout(anchor, size, dx, dy, a.LabelOffset);
                break;
            default:
                var center = style.Placement switch
                {
                    LabelPlacement.Outside => anchor + new Vector(dx * (size.Width / 2 + gap), dy * (size.Height / 2 + gap)),
                    LabelPlacement.Inside => anchor - new Vector(dx * (size.Width / 2 + gap), dy * (size.Height / 2 + gap)),
                    _ => anchor,
                };
                center += a.LabelOffset;
                box = new Rect(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
                body = BadgeGeometry(box, style.Shape);
                break;
        }

        var colors = ResolveColors(a.StrokeColor, style, box, sampleLuminance);
        return Finish(ft, style, box, body, colors);
    }

    /// <summary>Lays out a standalone badge (sidebar, export panel) centered on a point.</summary>
    public static LabelLayout LayoutBadge(Annotation a, string text, Point center, double pixelsPerDip, double fontSize)
    {
        var style = a.Label with
        {
            FontSize = fontSize,
            Shape = a.Label.Shape is LabelShape.CornerTab or LabelShape.Callout ? LabelShape.Box : a.Label.Shape,
            Scheme = a.Label.Scheme == LabelColorScheme.Adaptive ? LabelColorScheme.MatchShape : a.Label.Scheme,
        };
        var ft = CreateText(text, style, pixelsPerDip);
        var size = BodySize(ft, style);
        var box = new Rect(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
        var colors = ResolveColors(a.StrokeColor, style, box, null);
        return Finish(ft, style, box, BadgeGeometry(box, style.Shape), colors);
    }

    public static Size MeasureBadge(Annotation a, string text, double pixelsPerDip, double fontSize)
        => LayoutBadge(a, text, new Point(), pixelsPerDip, fontSize).Box.Size;

    public static void Draw(DrawingContext dc, LabelLayout layout)
    {
        var style = layout.Style;
        double fs = style.FontSize;

        if (layout.Body is null)
        {
            // Halo text: thick contrasting outline under the glyphs.
            var glyphs = layout.Text.BuildGeometry(layout.TextOrigin);
            if (style.Shadow)
            {
                dc.PushTransform(new TranslateTransform(fs * 0.05, fs * 0.08));
                dc.DrawGeometry(ShadowBrush, Pen(ShadowBrush.Color, fs * 0.26), glyphs);
                dc.Pop();
            }
            dc.DrawGeometry(null, Pen(layout.Colors.Border, fs * (style.Outline ? 0.26 : 0.16)), glyphs);
            dc.DrawGeometry(ColorUtil.Brush(layout.Colors.Fill), null, glyphs);
            return;
        }

        if (style.Shadow)
        {
            dc.PushTransform(new TranslateTransform(fs * 0.06, fs * 0.1));
            dc.DrawGeometry(ShadowBrush, null, layout.Body);
            dc.Pop();
        }
        var border = style.Outline ? Pen(layout.Colors.Border, Math.Max(1, fs * 0.1)) : null;
        dc.DrawGeometry(ColorUtil.Brush(layout.Colors.Fill), border, layout.Body);
        dc.DrawText(layout.Text, layout.TextOrigin);
    }

    public static LabelColors ResolveColors(Color shapeColor, LabelStyle style, Rect box, Func<Rect, double?>? sampleLuminance)
    {
        switch (style.Scheme)
        {
            case LabelColorScheme.Adaptive:
                double lum = sampleLuminance?.Invoke(box) ?? 0.5;
                return lum > 0.45
                    ? new LabelColors(ColorUtil.Ink, Colors.White, Colors.White)
                    : new LabelColors(Colors.White, ColorUtil.Ink, ColorUtil.Ink);
            case LabelColorScheme.Light:
                return new LabelColors(Colors.White, ColorUtil.Ink, ColorUtil.Ink);
            case LabelColorScheme.Dark:
                return new LabelColors(ColorUtil.Ink, Colors.White, Colors.White);
            case LabelColorScheme.Custom:
                return new LabelColors(style.CustomFill, style.CustomText, ColorUtil.ContrastOutline(style.CustomFill));
            default:
                var fill = ColorUtil.WithAlpha(shapeColor, 0xFF);
                return new LabelColors(fill, ColorUtil.BestTextOn(fill), ColorUtil.ContrastOutline(fill));
        }
    }

    private static LabelLayout Finish(FormattedText ft, LabelStyle style, Rect box, Geometry? body, LabelColors colors)
    {
        ft.SetForegroundBrush(ColorUtil.Brush(body is null ? colors.Fill : colors.Text));
        double cap = CapHeight(style);
        double baseline = box.Top + box.Height / 2 + cap / 2;
        var origin = new Point(box.Left + (box.Width - ft.WidthIncludingTrailingWhitespace) / 2, baseline - ft.Baseline);
        if (body is { IsFrozen: false }) body.Freeze();
        return new LabelLayout { Box = box, Body = body, Text = ft, TextOrigin = origin, Colors = colors, Style = style };
    }

    private static FormattedText CreateText(string text, LabelStyle style, double pixelsPerDip)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
               style.Bold ? BoldFace : RegularFace, Math.Max(1, style.FontSize), Brushes.Black, pixelsPerDip);

    private static double CapHeight(LabelStyle style) => (style.Bold ? BoldFace : RegularFace).CapsHeight * style.FontSize;

    private static Size BodySize(FormattedText ft, LabelStyle style)
    {
        double fs = style.FontSize;
        double tw = ft.WidthIncludingTrailingWhitespace;
        double cap = CapHeight(style);
        switch (style.Shape)
        {
            case LabelShape.Circle:
            {
                double h = cap + fs;
                return new Size(Math.Max(h, tw + fs * 0.55), h);
            }
            case LabelShape.CornerTab:
            {
                double h = cap + fs * 0.76;
                return new Size(Math.Max(h, tw + fs * 1.0), h);
            }
            case LabelShape.Callout:
            {
                double h = cap + fs * 1.1;
                return new Size(Math.Max(h * 1.4, tw + fs * 1.4), h);
            }
            case LabelShape.Halo:
                return new Size(tw + fs * 0.3, cap + fs * 0.5);
            default:
            {
                double h = cap + fs * 0.76;
                return new Size(Math.Max(h, tw + fs * 0.8), h);
            }
        }
    }

    private static Geometry? BadgeGeometry(Rect box, LabelShape shape) => shape switch
    {
        LabelShape.Circle => new RectangleGeometry(box, box.Height / 2, box.Height / 2),
        LabelShape.Halo => null,
        _ => new RectangleGeometry(box, box.Height * 0.16, box.Height * 0.16),
    };

    private static (int Dx, int Dy) Direction(LabelAnchor anchor) => anchor switch
    {
        LabelAnchor.TopLeft => (-1, -1),
        LabelAnchor.Top => (0, -1),
        LabelAnchor.TopRight => (1, -1),
        LabelAnchor.Right => (1, 0),
        LabelAnchor.BottomRight => (1, 1),
        LabelAnchor.Bottom => (0, 1),
        LabelAnchor.BottomLeft => (-1, 1),
        LabelAnchor.Left => (-1, 0),
        _ => (0, 0),
    };

    /// <summary>The point on the shape outline the label is attached to.</summary>
    private static Point AnchorPoint(Annotation a, Rect b, int dx, int dy)
    {
        var center = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        var target = new Point(center.X + dx * b.Width / 2, center.Y + dy * b.Height / 2);
        switch (a.Kind)
        {
            case ShapeKind.Ellipse or ShapeKind.Circle when dx != 0 && dy != 0:
                const double diagonal = 0.70710678;
                return new Point(center.X + dx * b.Width / 2 * diagonal, center.Y + dy * b.Height / 2 * diagonal);
            case ShapeKind.Spline when (dx != 0 || dy != 0) && a.Points.Length > 1:
                return Spline.Nearest(a.Points, a.IsClosed, target, 12).Point;
            default:
                return target;
        }
    }

    private static Rect TabBox(Rect b, Size size, LabelStyle style, int dx, int dy, double strokeWidth)
    {
        bool bottom = dy > 0;
        bool inside = style.Placement == LabelPlacement.Inside;
        double half = strokeWidth / 2;
        double x = dx < 0 ? (inside ? b.Left + half : b.Left - half)
                 : dx > 0 ? (inside ? b.Right - half - size.Width : b.Right + half - size.Width)
                 : b.Left + (b.Width - size.Width) / 2;
        double y = bottom
            ? (inside ? b.Bottom - half - size.Height : b.Bottom + half)
            : (inside ? b.Top + half : b.Top - half - size.Height);
        return new Rect(x, y, size.Width, size.Height);
    }

    private static Geometry TabGeometry(Rect box, LabelStyle style, int dy)
    {
        bool bottom = dy > 0;
        bool inside = style.Placement == LabelPlacement.Inside;
        double r = box.Height * 0.3;
        bool roundTop = bottom == inside;
        return roundTop
            ? ShapeGeometry.RoundedRect(box, r, r, 0, 0)
            : ShapeGeometry.RoundedRect(box, 0, 0, r, r);
    }

    private static (Rect Box, Geometry Body) Callout(Point anchor, Size size, int dx, int dy, Vector offset)
    {
        var dir = new Vector(dx, dy);
        if (dir.LengthSquared < 0.5) dir = new Vector(0, -1);
        dir.Normalize();

        double hw = size.Width / 2, hh = size.Height / 2;
        double edgeX = Math.Abs(dir.X) > 1e-6 ? hw / Math.Abs(dir.X) : double.MaxValue;
        double edgeY = Math.Abs(dir.Y) > 1e-6 ? hh / Math.Abs(dir.Y) : double.MaxValue;
        double tail = size.Height * 1.1;
        var c = anchor + dir * (Math.Min(edgeX, edgeY) + tail) + offset;
        var box = new Rect(c.X - hw, c.Y - hh, size.Width, size.Height);
        double radius = size.Height * 0.32;
        Geometry bubble = new RectangleGeometry(box, radius, radius);

        var toAnchor = anchor - c;
        var inflated = box;
        inflated.Inflate(1, 1);
        if (inflated.Contains(anchor) || toAnchor.Length < 1) return (box, bubble);

        var normal = new Vector(-toAnchor.Y, toAnchor.X);
        normal.Normalize();
        double baseHalf = Math.Min(size.Height * 0.3, size.Width * 0.3);
        var tail3 = new StreamGeometry();
        using (var ctx = tail3.Open())
        {
            ctx.BeginFigure(c + normal * baseHalf, true, true);
            ctx.LineTo(anchor, true, false);
            ctx.LineTo(c - normal * baseHalf, true, false);
        }
        return (box, new CombinedGeometry(GeometryCombineMode.Union, bubble, tail3));
    }

    private static Pen Pen(Color color, double width)
    {
        var pen = new Pen(ColorUtil.Brush(color), width) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush CreateFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
