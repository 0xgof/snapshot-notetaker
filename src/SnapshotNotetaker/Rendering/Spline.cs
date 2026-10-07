using System.Windows;
using System.Windows.Media;

namespace SnapshotNotetaker.Rendering;

/// <summary>
/// Centripetal Catmull-Rom spline through a list of points, emitted as cubic Bézier segments.
/// Centripetal parameterisation (alpha = 0.5) avoids cusps and self-intersections on uneven spacing.
/// </summary>
public static class Spline
{
    private const double Alpha = 0.5;
    private const double Epsilon = 1e-6;

    public readonly record struct Segment(Point Start, Point C1, Point C2, Point End);

    public readonly record struct NearestPoint(int Segment, double T, Point Point, double Distance);

    public static List<Segment> Segments(IReadOnlyList<Point> pts, bool closed)
    {
        int n = pts.Count;
        var list = new List<Segment>(Math.Max(0, n));
        if (n < 2) return list;
        closed = closed && n > 2;

        int count = closed ? n : n - 1;
        for (int i = 0; i < count; i++)
        {
            Point p1 = pts[i];
            Point p2 = pts[(i + 1) % n];
            Point p0 = closed ? pts[(i - 1 + n) % n] : pts[Math.Max(i - 1, 0)];
            Point p3 = closed ? pts[(i + 2) % n] : pts[Math.Min(i + 2, n - 1)];
            var (c1, c2) = ControlPoints(p0, p1, p2, p3);
            list.Add(new Segment(p1, c1, c2, p2));
        }
        return list;
    }

    public static StreamGeometry Build(IReadOnlyList<Point> pts, bool closed)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (pts.Count > 0)
            {
                bool isClosed = closed && pts.Count > 2;
                ctx.BeginFigure(pts[0], isClosed, isClosed);
                if (pts.Count == 1) ctx.LineTo(pts[0], true, false);
                foreach (var s in Segments(pts, isClosed)) ctx.BezierTo(s.C1, s.C2, s.End, true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    public static (Point C1, Point C2) ControlPoints(Point p0, Point p1, Point p2, Point p3)
    {
        double d01 = Math.Pow((p1 - p0).LengthSquared, Alpha / 2); // |p1-p0|^alpha
        double d12 = Math.Pow((p2 - p1).LengthSquared, Alpha / 2);
        double d23 = Math.Pow((p3 - p2).LengthSquared, Alpha / 2);
        double d01Sq = d01 * d01, d12Sq = d12 * d12, d23Sq = d23 * d23;

        Point c1 = p1;
        if (d01 > Epsilon)
        {
            double a = 2 * d01Sq + 3 * d01 * d12 + d12Sq;
            double n = 3 * d01 * (d01 + d12);
            c1 = new Point((p1.X * a - p0.X * d12Sq + p2.X * d01Sq) / n,
                           (p1.Y * a - p0.Y * d12Sq + p2.Y * d01Sq) / n);
        }

        Point c2 = p2;
        if (d23 > Epsilon)
        {
            double b = 2 * d23Sq + 3 * d23 * d12 + d12Sq;
            double m = 3 * d23 * (d23 + d12);
            c2 = new Point((p2.X * b + p1.X * d23Sq - p3.X * d12Sq) / m,
                           (p2.Y * b + p1.Y * d23Sq - p3.Y * d12Sq) / m);
        }

        return (c1, c2);
    }

    public static Point Evaluate(Segment s, double t)
    {
        double u = 1 - t;
        double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        return new Point(a * s.Start.X + b * s.C1.X + c * s.C2.X + d * s.End.X,
                         a * s.Start.Y + b * s.C1.Y + c * s.C2.Y + d * s.End.Y);
    }

    /// <summary>Closest point on the curve to <paramref name="target"/> (sampled, then refined).</summary>
    public static NearestPoint Nearest(IReadOnlyList<Point> pts, bool closed, Point target, int steps = 24)
    {
        if (pts.Count == 0) return new NearestPoint(-1, 0, target, double.MaxValue);
        if (pts.Count == 1) return new NearestPoint(-1, 0, pts[0], (pts[0] - target).Length);

        var segments = Segments(pts, closed);
        var best = new NearestPoint(-1, 0, pts[0], double.MaxValue);
        for (int i = 0; i < segments.Count; i++)
        {
            for (int k = 0; k <= steps; k++)
            {
                double t = (double)k / steps;
                var p = Evaluate(segments[i], t);
                double d = (p - target).LengthSquared;
                if (d < best.Distance) best = new NearestPoint(i, t, p, d);
            }
        }

        // Refine around the best sample with a small ternary-style search.
        var seg = segments[best.Segment];
        double lo = Math.Max(0, best.T - 1.0 / steps), hi = Math.Min(1, best.T + 1.0 / steps);
        for (int iter = 0; iter < 20; iter++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if ((Evaluate(seg, m1) - target).LengthSquared < (Evaluate(seg, m2) - target).LengthSquared) hi = m2;
            else lo = m1;
        }
        double tt = (lo + hi) / 2;
        var refined = Evaluate(seg, tt);
        double refinedDist = (refined - target).LengthSquared;
        if (refinedDist < best.Distance) best = new NearestPoint(best.Segment, tt, refined, refinedDist);

        return best with { Distance = Math.Sqrt(best.Distance) };
    }

    /// <summary>Ramer–Douglas–Peucker simplification (used to turn freehand strokes into editable splines).</summary>
    public static List<Point> Simplify(IReadOnlyList<Point> pts, double tolerance)
    {
        if (pts.Count < 3) return pts.ToList();
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double maxDistance = 0;
            int index = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = DistanceToSegment(pts[i], pts[a], pts[b]);
                if (d > maxDistance) { maxDistance = d; index = i; }
            }
            if (index >= 0 && maxDistance > tolerance)
            {
                keep[index] = true;
                stack.Push((a, index));
                stack.Push((index, b));
            }
        }

        var result = new List<Point>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
        return result;
    }

    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double lengthSquared = ab.LengthSquared;
        if (lengthSquared < Epsilon) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / lengthSquared, 0, 1);
        return (p - (a + t * ab)).Length;
    }
}
