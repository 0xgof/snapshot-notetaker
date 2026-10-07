using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapshotNotetaker.IO;
using SnapshotNotetaker.Model;
using SnapshotNotetaker.Rendering;
using Xunit;

namespace SnapshotNotetaker.Tests;

public class SplineTests
{
    private static readonly Point[] Loop = { new(0, 0), new(100, 10), new(120, 90), new(30, 110) };

    [Fact]
    public void CurvePassesThroughEveryPoint()
    {
        var segments = Spline.Segments(Loop, closed: true);
        Assert.Equal(Loop.Length, segments.Count);
        for (int i = 0; i < Loop.Length; i++)
        {
            Assert.Equal(Loop[i], segments[i].Start);
            Assert.Equal(Loop[(i + 1) % Loop.Length], segments[i].End);
        }
        Assert.Equal(Loop.Length - 1, Spline.Segments(Loop, closed: false).Count);
    }

    [Fact]
    public void CollinearPointsGiveAStraightLine()
    {
        var pts = new[] { new Point(0, 0), new Point(1, 0), new Point(2, 0), new Point(3, 0) };
        var (c1, c2) = Spline.ControlPoints(pts[0], pts[1], pts[2], pts[3]);
        Assert.Equal(4.0 / 3, c1.X, 6);
        Assert.Equal(5.0 / 3, c2.X, 6);
        Assert.Equal(0, c1.Y, 6);
    }

    [Fact]
    public void NearestFindsPointOnCurve()
    {
        var hit = Spline.Nearest(Loop, true, new Point(100, 10.5));
        Assert.True(hit.Distance < 1, $"distance {hit.Distance}");
    }

    [Fact]
    public void SimplifyKeepsCornersAndDropsNoise()
    {
        var raw = Enumerable.Range(0, 101).Select(i => new Point(i, i % 2 == 0 ? 0 : 0.3)).Concat(Enumerable.Range(1, 100).Select(i => new Point(100, i))).ToList();
        var simplified = Spline.Simplify(raw, 1.0);
        Assert.Equal(3, simplified.Count);
        Assert.Equal(new Point(100, 0), simplified[1]);
    }
}

public class LabelTests
{
    private static Annotation Box(LabelStyle style) => new()
    {
        Kind = ShapeKind.Rectangle,
        Bounds = new Rect(100, 100, 200, 100),
        StrokeWidth = 2,
        Label = style,
        NumberText = "7",
    };

    [Fact]
    public void OnEdgeLabelIsCenteredOnTheCorner() => Sta.Run(() =>
    {
        var layout = LabelRenderer.Layout(Box(new LabelStyle { Anchor = LabelAnchor.TopLeft, Placement = LabelPlacement.OnEdge }), 1)!;
        var center = new Point(layout.Box.X + layout.Box.Width / 2, layout.Box.Y + layout.Box.Height / 2);
        Assert.Equal(100, center.X, 3);
        Assert.Equal(100, center.Y, 3);
    });

    [Fact]
    public void OutsideLabelDoesNotOverlapTheShape() => Sta.Run(() =>
    {
        var layout = LabelRenderer.Layout(Box(new LabelStyle { Anchor = LabelAnchor.BottomRight, Placement = LabelPlacement.Outside }), 1)!;
        Assert.True(layout.Box.Left >= 300 && layout.Box.Top >= 200, layout.Box.ToString());
    });

    [Fact]
    public void InsideLabelStaysInside() => Sta.Run(() =>
    {
        var layout = LabelRenderer.Layout(Box(new LabelStyle { Anchor = LabelAnchor.TopRight, Placement = LabelPlacement.Inside }), 1)!;
        Assert.True(new Rect(100, 100, 200, 100).Contains(layout.Box), layout.Box.ToString());
    });

    [Fact]
    public void CornerTabSitsOnTopEdge() => Sta.Run(() =>
    {
        var layout = LabelRenderer.Layout(Box(new LabelStyle { Shape = LabelShape.CornerTab, Anchor = LabelAnchor.TopLeft, Placement = LabelPlacement.Outside }), 1)!;
        Assert.Equal(99, layout.Box.Left, 3);   // flush with the outer edge of a 2px stroke
        Assert.Equal(99, layout.Box.Bottom, 3);
    });

    [Fact]
    public void CalloutHasATailReachingTheShape() => Sta.Run(() =>
    {
        var layout = LabelRenderer.Layout(Box(new LabelStyle { Shape = LabelShape.Callout, Anchor = LabelAnchor.Right }), 1)!;
        Assert.True(layout.Box.Left > 300, "bubble should sit to the right of the shape");
        Assert.True(layout.Body!.Bounds.Left <= 300.5, "tail should reach the anchor on the right edge");
    });

    [Fact]
    public void AdaptiveSchemeContrastsWithBackground()
    {
        var style = new LabelStyle { Scheme = LabelColorScheme.Adaptive };
        var onLight = LabelRenderer.ResolveColors(Colors.Red, style, new Rect(), _ => 0.95);
        var onDark = LabelRenderer.ResolveColors(Colors.Red, style, new Rect(), _ => 0.05);
        Assert.True(ColorUtil.Luminance(onLight.Fill) < 0.1);
        Assert.True(ColorUtil.Luminance(onDark.Fill) > 0.9);
    }

    [Fact]
    public void NoLabelWhenModeIsNone() => Sta.Run(() =>
    {
        var a = Box(new LabelStyle());
        a.LabelMode = LabelMode.None;
        Assert.Null(LabelRenderer.Layout(a, 1));
    });
}

public class PersistenceTests
{
    private static AnnotationDocument SampleDoc(string id)
    {
        var pixels = new byte[32 * 20 * 4];
        new Random(1).NextBytes(pixels);
        var image = BitmapSource.Create(32, 20, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        image.Freeze();
        var doc = new AnnotationDocument(id, image, 1.5, "Region 32×20", new DateTime(2026, 10, 7, 9, 30, 0), "Sample");
        doc.Annotations.Add(new Annotation { Kind = ShapeKind.Circle, Bounds = new Rect(1, 2, 10, 10), StrokeColor = Colors.Teal, Comment = "first", Tag = "A" });
        doc.Annotations.Add(new Annotation
        {
            Kind = ShapeKind.Spline, IsClosed = true, Points = new[] { new Point(1, 1), new Point(20, 2), new Point(15, 18) },
            LabelMode = LabelMode.NumberAndTag, Tag = "loop", LabelOffset = new Vector(3, -4),
            Label = new LabelStyle { Shape = LabelShape.Callout, Scheme = LabelColorScheme.Custom, CustomFill = Colors.Gold, FontSize = 18 },
        });
        doc.Numbering = new NumberingOptions { Format = NumberFormat.LowerRoman, Prefix = "#" };
        return doc;
    }

    [Fact]
    public void SnapnoteRoundTrip() => Sta.Run(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"sn-test-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            var doc = SampleDoc("x");
            ProjectFile.Write(doc, path);
            var (image, file) = ProjectFile.Read(path);
            Assert.Equal(32, image.PixelWidth);
            Assert.Equal(doc.Numbering, file.Numbering);
            Assert.Equal(doc.Annotations.Select(a => a.ToData() with { Points = Array.Empty<Point>() }),
                         file.Annotations.Select(a => a with { Points = Array.Empty<Point>() }));
            Assert.Equal(doc.Annotations[1].Points, file.Annotations[1].Points);
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void LibraryPersistsSnapshotsAndEdits() => Sta.Run(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), $"sn-lib-{Guid.NewGuid():N}");
        try
        {
            var library = new SnapshotLibrary(root);
            var source = SampleDoc("ignored");
            var (entry, doc) = library.Create(source.Image, 1.25, "Window — Notepad", DateTime.Now);
            doc.Annotations.Add(new Annotation { Kind = ShapeKind.Rectangle, Bounds = new Rect(0, 0, 5, 5), Comment = "edit after capture" });
            doc.Title = "Renamed";
            library.Save(doc);
            library.Flush();
            Assert.True(File.Exists(entry.ImagePath));
            Assert.True(File.Exists(entry.ThumbnailPath));

            var reopened = new SnapshotLibrary(root);
            reopened.LoadAsync().GetAwaiter().GetResult();
            var found = Assert.Single(reopened.Entries);
            Assert.Equal("Renamed", found.Title);
            Assert.Equal(1, found.NoteCount);
            Assert.Contains("edit after capture", found.SearchText);

            var loaded = reopened.OpenAsync(found).GetAwaiter().GetResult();
            Assert.Equal(1.25, loaded.CaptureScale);
            Assert.Equal("edit after capture", Assert.Single(loaded.Annotations).Comment);
            Assert.Equal("1", loaded.Annotations[0].NumberText);
            Assert.False(loaded.IsDirty);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    });

    [Fact]
    public void ExportIncludesNotesPanel() => Sta.Run(() =>
    {
        var doc = SampleDoc("e");
        var plain = Exporter.Render(doc, new ExportOptions(NotesLayout.None));
        var right = Exporter.Render(doc, new ExportOptions(NotesLayout.Right));
        var below = Exporter.Render(doc, new ExportOptions(NotesLayout.Bottom));
        var margins = Exporter.Render(doc, new ExportOptions(NotesLayout.Margins));
        Assert.True(right.PixelWidth > plain.PixelWidth);
        Assert.True(below.PixelHeight > plain.PixelHeight);
        Assert.True(margins.PixelWidth > plain.PixelWidth && margins.PixelHeight > plain.PixelHeight);
        Assert.Contains("- **#i** **A** — first", Exporter.NotesAsMarkdown(doc));
    });
}

public class FileUtilTests
{
    [Fact]
    public async Task ReplaceWaitsOutABriefLockOnTheTarget()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sn-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string target = Path.Combine(dir, "document.json"), tmp = target + ".tmp";
            File.WriteAllText(target, "old");
            File.WriteAllText(tmp, "new");

            // Like an antivirus scan: the target is open without delete sharing for a moment.
            var locker = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            var release = Task.Run(async () => { await Task.Delay(150); locker.Dispose(); });

            FileUtil.ReplaceWith(tmp, target);
            await release;
            Assert.Equal("new", File.ReadAllText(target));
            Assert.False(File.Exists(tmp));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class NotesCompositionTests
{
    /// <summary>A 1000×600 snapshot with areas spread over both halves, several stacked at the same height.</summary>
    private static AnnotationDocument Crowded()
    {
        var image = BitmapSource.Create(1000, 600, 96, 96, PixelFormats.Bgr32, null, new byte[1000 * 600 * 4], 1000 * 4);
        image.Freeze();
        var doc = new AnnotationDocument("c", image, 1, "test", DateTime.Now, "Crowded");
        for (int i = 0; i < 4; i++)
        {
            doc.Annotations.Add(new Annotation { Kind = ShapeKind.Rectangle, Bounds = new Rect(60 + i * 20, 280, 80, 40), Comment = $"Left note {i} " + new string('x', i * 60) });
            doc.Annotations.Add(new Annotation { Kind = ShapeKind.Ellipse, Bounds = new Rect(700, 100 + i * 30, 120, 60), Comment = $"Right note {i}" });
        }
        doc.Annotations.Add(new Annotation { Kind = ShapeKind.Circle, Bounds = new Rect(450, 250, 50, 50) }); // no comment: no card
        return doc;
    }

    [Fact]
    public void MarginCardsSitOutsideTheImageWithoutOverlapping() => Sta.Run(() =>
    {
        var doc = Crowded();
        var c = NotesComposition.Build(doc, NotesLayout.Margins, _ => null, 1);

        Assert.Equal(8, c.Notes.Count);
        foreach (var (rect, a) in c.Notes)
        {
            Assert.False(rect.IntersectsWith(c.ImageRect), $"card for “{a.Comment}” overlaps the image");
            Assert.True(c.Bounds.Contains(rect));
            bool areaOnLeft = a.ShapeBounds.X + a.ShapeBounds.Width / 2 < 500;
            Assert.Equal(areaOnLeft, rect.Right <= c.ImageRect.Left);
        }
        var cards = c.Notes.Select(n => n.Rect).ToList();
        for (int i = 0; i < cards.Count; i++)
            for (int j = i + 1; j < cards.Count; j++)
                Assert.False(cards[i].IntersectsWith(cards[j]), $"cards {i} and {j} overlap");
    });

    [Fact]
    public void ListLayoutsAddAPanelBesideOrBelow() => Sta.Run(() =>
    {
        var doc = Crowded();
        var right = NotesComposition.Build(doc, NotesLayout.Right, _ => null, 1);
        var below = NotesComposition.Build(doc, NotesLayout.Bottom, _ => null, 1);
        Assert.True(right.Notes.All(n => n.Rect.Left >= 1000));
        Assert.True(below.Notes.All(n => n.Rect.Top >= 600));
        Assert.Equal(9, right.Notes.Count); // the uncommented but numbered circle is listed too
        Assert.Same(doc.Annotations[0], right.HitTest(new Point(right.Notes[0].Rect.X + 5, right.Notes[0].Rect.Y + 5)));
    });

    [Fact]
    public void ImageOnlyKeepsTheImageBounds() => Sta.Run(() =>
    {
        var c = NotesComposition.Build(Crowded(), NotesLayout.None, _ => null, 1);
        Assert.Equal(new Rect(0, 0, 1000, 600), c.Bounds);
        Assert.Empty(c.Notes);
    });
}
