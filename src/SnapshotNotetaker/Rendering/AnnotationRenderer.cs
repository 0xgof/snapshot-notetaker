using System.Windows.Media;
using SnapshotNotetaker.Model;

namespace SnapshotNotetaker.Rendering;

public static class AnnotationRenderer
{
    public static void DrawShape(DrawingContext dc, Annotation a)
    {
        var geometry = a.Geometry;
        if (a.FillOpacity > 0 && (!a.IsSpline || a.IsClosed))
        {
            byte alpha = (byte)Math.Clamp(a.FillOpacity * a.StrokeColor.A, 0, 255);
            dc.DrawGeometry(ColorUtil.Brush(ColorUtil.WithAlpha(a.StrokeColor, alpha)), null, geometry);
        }

        if (a.StrokeOutline)
        {
            var outline = ColorUtil.WithAlpha(ColorUtil.ContrastOutline(a.StrokeColor), 0xC0);
            dc.DrawGeometry(null, CreatePen(outline, a.StrokeWidth + Math.Max(2, a.StrokeWidth * 0.9), a.Dash), geometry);
        }

        dc.DrawGeometry(null, CreatePen(a.StrokeColor, a.StrokeWidth, a.Dash), geometry);
    }

    public static Pen CreatePen(Color color, double width, StrokeDash dash)
    {
        var pen = new Pen(ColorUtil.Brush(color), Math.Max(0.1, width))
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        switch (dash)
        {
            case StrokeDash.Dashed:
                pen.DashStyle = new DashStyle(new[] { 3.0, 2.0 }, 0);
                pen.DashCap = PenLineCap.Flat;
                break;
            case StrokeDash.Dotted:
                pen.DashStyle = new DashStyle(new[] { 0.0, 2.0 }, 0);
                pen.DashCap = PenLineCap.Round;
                break;
        }
        pen.Freeze();
        return pen;
    }
}
