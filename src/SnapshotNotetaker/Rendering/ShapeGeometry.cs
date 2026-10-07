using System.Windows;
using System.Windows.Media;
using SnapshotNotetaker.Model;

namespace SnapshotNotetaker.Rendering;

public static class ShapeGeometry
{
    public static Geometry Build(Annotation a)
    {
        Geometry g = a.Kind switch
        {
            ShapeKind.Rectangle or ShapeKind.Square => new RectangleGeometry(a.Bounds),
            ShapeKind.Ellipse or ShapeKind.Circle => new EllipseGeometry(a.Bounds),
            _ => Spline.Build(a.Points, a.IsClosed),
        };
        if (!g.IsFrozen) g.Freeze();
        return g;
    }

    /// <summary>Rounded rectangle with an individual radius per corner.</summary>
    public static Geometry RoundedRect(Rect r, double topLeft, double topRight, double bottomRight, double bottomLeft)
    {
        double max = Math.Min(r.Width, r.Height) / 2;
        topLeft = Math.Min(topLeft, max);
        topRight = Math.Min(topRight, max);
        bottomRight = Math.Min(bottomRight, max);
        bottomLeft = Math.Min(bottomLeft, max);

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(r.Left + topLeft, r.Top), true, true);
            ctx.LineTo(new Point(r.Right - topRight, r.Top), true, false);
            if (topRight > 0) ctx.ArcTo(new Point(r.Right, r.Top + topRight), new Size(topRight, topRight), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.Right, r.Bottom - bottomRight), true, false);
            if (bottomRight > 0) ctx.ArcTo(new Point(r.Right - bottomRight, r.Bottom), new Size(bottomRight, bottomRight), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.Left + bottomLeft, r.Bottom), true, false);
            if (bottomLeft > 0) ctx.ArcTo(new Point(r.Left, r.Bottom - bottomLeft), new Size(bottomLeft, bottomLeft), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.Left, r.Top + topLeft), true, false);
            if (topLeft > 0) ctx.ArcTo(new Point(r.Left + topLeft, r.Top), new Size(topLeft, topLeft), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
