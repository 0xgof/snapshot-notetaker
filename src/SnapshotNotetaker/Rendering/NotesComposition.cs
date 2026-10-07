using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SnapshotNotetaker.Model;

namespace SnapshotNotetaker.Rendering;

/// <summary>
/// Layout of an "expanded" snapshot: the image plus its tags and comments, so a single picture can be handed over.
/// <list type="bullet">
/// <item><see cref="NotesLayout.Margins"/>: a card per comment in the left/right margin, level with its area, joined by a leader line.</item>
/// <item><see cref="NotesLayout.Right"/> / <see cref="NotesLayout.Bottom"/>: a numbered list beside or below the image.</item>
/// </list>
/// Everything is in image coordinates (the image occupies 0,0 – W,H; margins extend beyond), so the editor can draw
/// it live around the image and the exporter can render it 1:1.
/// </summary>
public sealed class NotesComposition
{
    private static readonly Typeface Regular = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Color Backdrop = Color.FromRgb(0xEC, 0xEE, 0xF2);
    private static readonly Color PanelBackground = Color.FromRgb(0xF7, 0xF8, 0xFA);
    private static readonly Color Ink = Color.FromRgb(0x1F, 0x23, 0x28);
    private static readonly Color Muted = Color.FromRgb(0x6B, 0x72, 0x80);
    private static readonly Color Rule = Color.FromRgb(0xE3, 0xE6, 0xEA);
    private static readonly Color CardBorder = Color.FromRgb(0xD3, 0xD8, 0xDF);

    private readonly List<Action<DrawingContext>> _under = new();
    private readonly List<Action<DrawingContext>> _over = new();
    private readonly List<(Rect Rect, Annotation Annotation)> _notes = new();

    private NotesComposition(NotesLayout layout, Rect image, Rect content)
    {
        Layout = layout;
        ImageRect = image;
        Content = content;
        Bounds = content;
    }

    public NotesLayout Layout { get; }

    /// <summary>The snapshot image: (0, 0, width, height).</summary>
    public Rect ImageRect { get; }

    /// <summary>The image grown to include shapes and labels that hang off its edges.</summary>
    public Rect Content { get; }

    /// <summary>The whole composed picture.</summary>
    public Rect Bounds { get; private set; }

    /// <summary>Where each note (card or list row) was placed.</summary>
    public IReadOnlyList<(Rect Rect, Annotation Annotation)> Notes => _notes;

    public Annotation? HitTest(Point p)
    {
        for (int i = _notes.Count - 1; i >= 0; i--)
            if (_notes[i].Rect.Contains(p)) return _notes[i].Annotation;
        return null;
    }

    /// <summary>Backdrop, header and note cards/list. Drawn before (under) the image.</summary>
    public void DrawUnder(DrawingContext dc)
    {
        dc.DrawRectangle(ColorUtil.Brush(Backdrop), null, Bounds);
        foreach (var draw in _under) draw(dc);
    }

    /// <summary>Leader lines. Drawn over the image but under shapes and labels.</summary>
    public void DrawOver(DrawingContext dc)
    {
        foreach (var draw in _over) draw(dc);
    }

    /// <summary>Areas worth listing: anything with a comment, a tag, or a visible label.</summary>
    public static List<Annotation> NoteItems(AnnotationDocument doc)
        => doc.Annotations.Where(a => a.LabelMode != LabelMode.None || a.Comment.Trim().Length > 0 || a.Tag.Trim().Length > 0).ToList();

    public static NotesComposition Build(AnnotationDocument doc, NotesLayout layout, Func<Annotation, LabelLayout?> labelOf, double pixelsPerDip)
    {
        var image = new Rect(0, 0, doc.PixelWidth, doc.PixelHeight);
        var content = image;
        foreach (var a in doc.Annotations)
        {
            var b = a.ShapeBounds;
            if (!b.IsEmpty)
            {
                b.Inflate(a.StrokeWidth, a.StrokeWidth);
                content.Union(b);
            }
            if (labelOf(a) is { } label)
            {
                var lb = label.Bounds;
                lb.Inflate(label.Style.FontSize * 0.2, label.Style.FontSize * 0.2);
                content.Union(lb);
            }
        }

        var composition = new NotesComposition(layout, image, Snap(content));
        double s = Math.Max(1, doc.CaptureScale);
        switch (layout)
        {
            case NotesLayout.Margins:
                composition.BuildMargins(doc, s, pixelsPerDip);
                break;
            case NotesLayout.Right:
            case NotesLayout.Bottom:
                composition.BuildList(doc, s, pixelsPerDip);
                break;
        }
        composition.Bounds = Snap(composition.Bounds);
        return composition;
    }

    // ------------------------------------------------------------------ margin cards

    private sealed class Card
    {
        public required Annotation Annotation { get; init; }
        public required bool Left { get; init; }
        public required LabelLayout? Badge { get; init; }
        public required double BadgeColumn { get; init; }
        public required FormattedText? Title { get; init; }
        public required FormattedText? Comment { get; init; }
        public required double Height { get; init; }
        public required double Desired { get; init; }
        public Rect Rect { get; set; }
    }

    private void BuildMargins(AnnotationDocument doc, double s, double ppd)
    {
        var items = doc.Annotations.Where(a => (a.Comment.Trim().Length > 0 || a.Tag.Trim().Length > 0) && !a.ShapeBounds.IsEmpty).ToList();
        double pad = 24 * s, gap = 44 * s, cardPad = 11 * s, cardGap = 12 * s;
        double cardWidth = Math.Clamp(Content.Width * 0.24, 250 * s, 380 * s);
        double badgeFont = 12.5 * s;
        double centerX = Content.X + Content.Width / 2;

        var cards = new List<Card>();
        foreach (var a in items)
        {
            var b = a.ShapeBounds;
            string number = a.HasNumber ? a.NumberText : "";
            var badge = number.Length > 0 ? LabelRenderer.LayoutBadge(a, number, new Point(), ppd, badgeFont) : null;
            double badgeColumn = (badge?.Box.Width ?? 10 * s) + 10 * s;
            double textWidth = cardWidth - cardPad * 2 - badgeColumn - 4 * s;
            string tag = a.Tag.Trim(), comment = a.Comment.Trim();
            var title = tag.Length > 0 ? Text(tag, Bold, 13.5 * s, Ink, ppd, textWidth) : null;
            var body = comment.Length > 0 ? Text(comment, Regular, 12.5 * s, Ink, ppd, textWidth) : null;
            double textHeight = (title?.Height ?? 0) + (title != null && body != null ? 3 * s : 0) + (body?.Height ?? 0);
            cards.Add(new Card
            {
                Annotation = a,
                Left = b.X + b.Width / 2 < centerX,
                Badge = badge,
                BadgeColumn = badgeColumn,
                Title = title,
                Comment = body,
                Height = cardPad * 2 + Math.Max(badge?.Box.Height ?? 12 * s, textHeight),
                Desired = b.Y + b.Height / 2,
            });
        }

        // Stack each side top-to-bottom in the order of the areas, as close to level with them as overlaps allow.
        foreach (bool left in new[] { true, false })
        {
            double x = left ? Content.Left - gap - cardWidth : Content.Right + gap;
            double bottom = double.NegativeInfinity;
            foreach (var card in cards.Where(c => c.Left == left).OrderBy(c => c.Desired))
            {
                double top = Math.Max(Math.Max(card.Desired - card.Height / 2, Content.Top), bottom + cardGap);
                card.Rect = new Rect(x, top, cardWidth, card.Height);
                bottom = card.Rect.Bottom;
            }
        }

        var area = Content;
        foreach (var card in cards) area.Union(card.Rect);

        var heading = Text(doc.Title, Bold, 17 * s, Ink, ppd, Math.Max(area.Width, 300 * s));
        var sub = Text($"{doc.CapturedAt.ToString("f", CultureInfo.CurrentCulture)}  ·  {cards.Count} comment{(cards.Count == 1 ? "" : "s")}",
            Regular, 12 * s, Muted, ppd, Math.Max(area.Width, 300 * s));
        double header = pad + heading.Height + 3 * s + sub.Height + pad * 0.75;
        Bounds = new Rect(area.Left - pad, area.Top - header, area.Width + pad * 2, area.Height + header + pad);

        var headingOrigin = new Point(Bounds.Left + pad, Bounds.Top + pad);
        _under.Add(dc =>
        {
            dc.DrawText(heading, headingOrigin);
            dc.DrawText(sub, new Point(headingOrigin.X, headingOrigin.Y + heading.Height + 3 * s));
        });

        foreach (var card in cards)
        {
            var c = card;
            _notes.Add((c.Rect, c.Annotation));
            var accent = ColorUtil.WithAlpha(c.Annotation.StrokeColor, 0xFF);
            double radius = 7 * s;

            _under.Add(dc =>
            {
                var shadow = c.Rect;
                shadow.Offset(0, 1.5 * s);
                dc.DrawRoundedRectangle(ColorUtil.Brush(Color.FromArgb(0x1E, 0, 0, 0)), null, shadow, radius, radius);
                dc.DrawRoundedRectangle(ColorUtil.Brush(accent), null, c.Rect, radius, radius);
                dc.DrawRoundedRectangle(Brushes.White, null, new Rect(c.Rect.X + 4 * s, c.Rect.Y, c.Rect.Width - 4 * s, c.Rect.Height), radius, radius);
                dc.DrawRoundedRectangle(null, Pen(CardBorder, Math.Max(1, s * 0.8)), c.Rect, radius, radius);

                double x = c.Rect.X + cardPad + 4 * s, y = c.Rect.Y + cardPad;
                if (c.Badge != null)
                {
                    dc.PushTransform(new TranslateTransform(x + c.Badge.Box.Width / 2, y + c.Badge.Box.Height / 2));
                    LabelRenderer.Draw(dc, c.Badge);
                    dc.Pop();
                }
                else dc.DrawEllipse(ColorUtil.Brush(accent), null, new Point(x + 5 * s, y + 8 * s), 4.5 * s, 4.5 * s);

                double tx = x + c.BadgeColumn, ty = y;
                if (c.Title != null)
                {
                    dc.DrawText(c.Title, new Point(tx, ty));
                    ty += c.Title.Height + 3 * s;
                }
                if (c.Comment != null) dc.DrawText(c.Comment, new Point(tx, ty));
            });

            // Leader: out of the card, along the gutter, then to the nearest point of the area.
            double rowY = c.Rect.Y + cardPad + (c.Badge?.Box.Height ?? 16 * s) / 2;
            var start = new Point(c.Left ? c.Rect.Right : c.Rect.Left, rowY);
            var elbow = new Point(c.Left ? Content.Left - gap * 0.35 : Content.Right + gap * 0.35, rowY);
            var end = AttachPoint(c.Annotation, elbow);
            _over.Add(dc =>
            {
                var halo = Pen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), 4.2 * s);
                var line = Pen(accent, 1.8 * s);
                var path = new StreamGeometry();
                using (var ctx = path.Open())
                {
                    ctx.BeginFigure(start, false, false);
                    ctx.LineTo(elbow, true, true);
                    ctx.LineTo(end, true, true);
                }
                path.Freeze();
                dc.DrawGeometry(null, halo, path);
                dc.DrawGeometry(null, line, path);
                dc.DrawEllipse(ColorUtil.Brush(accent), Pen(Colors.White, 1.2 * s), end, 3.6 * s, 3.6 * s);
            });
        }
    }

    /// <summary>Point on the area's outline nearest to (or facing) <paramref name="from"/>.</summary>
    private static Point AttachPoint(Annotation a, Point from)
    {
        var b = a.ShapeBounds;
        var center = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        switch (a.Kind)
        {
            case ShapeKind.Ellipse or ShapeKind.Circle:
                double rx = b.Width / 2, ry = b.Height / 2;
                if (rx <= 0 || ry <= 0) return center;
                double theta = Math.Atan2((from.Y - center.Y) / ry, (from.X - center.X) / rx);
                return new Point(center.X + rx * Math.Cos(theta), center.Y + ry * Math.Sin(theta));
            case ShapeKind.Spline:
                return a.Points.Length switch
                {
                    0 => center,
                    1 => a.Points[0],
                    _ => Spline.Nearest(a.Points, a.IsClosed, from, 12).Point,
                };
            default:
                return new Point(Math.Clamp(from.X, b.Left, b.Right), Math.Clamp(from.Y, b.Top, b.Bottom));
        }
    }

    // ------------------------------------------------------------------ list panel

    private void BuildList(AnnotationDocument doc, double s, double ppd)
    {
        var items = NoteItems(doc);
        if (items.Count == 0) return;

        bool right = Layout == NotesLayout.Right;
        double width = right ? Math.Clamp(Content.Width * 0.34, 300 * s, 520 * s) : Content.Width;
        double pad = 22 * s, gap = 14 * s, badgeFont = 12.5 * s;
        double innerWidth = width - pad * 2;

        var heading = Text(doc.Title, Bold, 16 * s, Ink, ppd, innerWidth);
        var sub = Text($"{doc.CapturedAt.ToString("f", CultureInfo.CurrentCulture)}  ·  {items.Count} note{(items.Count == 1 ? "" : "s")}",
            Regular, 11.5 * s, Muted, ppd, innerWidth);

        var badgeTexts = items.Select(a => a.HasNumber ? a.NumberText : "").ToList();
        double badgeColumn = Math.Max(26 * s, items.Select((a, i) => badgeTexts[i].Length == 0 ? 0 : LabelRenderer.MeasureBadge(a, badgeTexts[i], ppd, badgeFont).Width).DefaultIfEmpty(0).Max());
        double textWidth = innerWidth - badgeColumn - 12 * s;

        var rows = new List<(Annotation A, LabelLayout? Badge, FormattedText? Title, FormattedText? Body, double Height)>();
        foreach (var (a, i) in items.Select((a, i) => (a, i)))
        {
            var badge = badgeTexts[i].Length > 0 ? LabelRenderer.LayoutBadge(a, badgeTexts[i], new Point(), ppd, badgeFont) : null;
            string tag = a.Tag.Trim(), comment = a.Comment.Trim();
            var title = tag.Length > 0 ? Text(tag, Bold, 13 * s, Ink, ppd, textWidth) : null;
            var body = comment.Length > 0 ? Text(comment, Regular, 12.5 * s, Ink, ppd, textWidth)
                : title == null ? Text("No comment", Regular, 12.5 * s, Muted, ppd, textWidth) : null;
            double h = Math.Max(badge?.Box.Height ?? 22 * s, (title?.Height ?? 0) + (title != null && body != null ? 3 * s : 0) + (body?.Height ?? 0));
            rows.Add((a, badge, title, body, h));
        }

        double contentHeight = pad + heading.Height + 4 * s + sub.Height + 18 * s + rows.Sum(r => r.Height) + gap * 2 * Math.Max(0, rows.Count - 1) + pad;
        var panel = right
            ? new Rect(Content.Right, Content.Top, width, Math.Max(Content.Height, contentHeight))
            : new Rect(Content.Left, Content.Bottom, width, contentHeight);
        var bounds = Content;
        bounds.Union(panel);
        Bounds = bounds;

        double x = panel.X + pad, y = panel.Y + pad;
        var headingAt = new Point(x, y);
        y += heading.Height + 4 * s;
        var subAt = new Point(x, y);
        y += sub.Height + 18 * s;

        var placed = new List<(Annotation A, LabelLayout? Badge, FormattedText? Title, FormattedText? Body, double Y, double Height)>();
        foreach (var row in rows)
        {
            placed.Add((row.A, row.Badge, row.Title, row.Body, y, row.Height));
            _notes.Add((new Rect(panel.X, y - gap / 2, panel.Width, row.Height + gap), row.A));
            y += row.Height + gap * 2;
        }

        _under.Add(dc =>
        {
            dc.DrawRectangle(ColorUtil.Brush(PanelBackground), null, panel);
            var edge = right ? new Rect(panel.X, panel.Y, Math.Max(1, s), panel.Height) : new Rect(panel.X, panel.Y, panel.Width, Math.Max(1, s));
            dc.DrawRectangle(ColorUtil.Brush(Rule), null, edge);
            dc.DrawText(heading, headingAt);
            dc.DrawText(sub, subAt);
            for (int i = 0; i < placed.Count; i++)
            {
                var (a, badge, title, body, top, h) = placed[i];
                if (badge != null)
                {
                    dc.PushTransform(new TranslateTransform(x + badgeColumn / 2, top + badge.Box.Height / 2));
                    LabelRenderer.Draw(dc, badge);
                    dc.Pop();
                }
                else dc.DrawEllipse(ColorUtil.Brush(a.StrokeColor), null, new Point(x + badgeColumn / 2, top + 10 * s), 5 * s, 5 * s);

                double tx = x + badgeColumn + 12 * s, ty = top;
                if (title != null) { dc.DrawText(title, new Point(tx, ty)); ty += title.Height + 3 * s; }
                if (body != null) dc.DrawText(body, new Point(tx, ty));
                if (i < placed.Count - 1)
                    dc.DrawRectangle(ColorUtil.Brush(Rule), null, new Rect(x, top + h + gap, innerWidth, Math.Max(1, s * 0.75)));
            }
        });
    }

    // ------------------------------------------------------------------ helpers

    private static FormattedText Text(string text, Typeface face, double size, Color color, double ppd, double maxWidth)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, ColorUtil.Brush(color), ppd)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            Trimming = TextTrimming.None,
        };
        return ft;
    }

    private static Pen Pen(Color color, double width)
    {
        var pen = new Pen(ColorUtil.Brush(color), width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static Rect Snap(Rect r)
    {
        double left = Math.Floor(r.Left), top = Math.Floor(r.Top);
        return new Rect(left, top, Math.Ceiling(r.Right) - left, Math.Ceiling(r.Bottom) - top);
    }
}
