using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapshotNotetaker.Model;
using SnapshotNotetaker.Rendering;

namespace SnapshotNotetaker.IO;

public sealed record ExportOptions(NotesLayout Layout);

/// <summary>Renders a document (image, shapes, labels and optionally its notes) to a bitmap at full resolution.</summary>
public static class Exporter
{
    public static BitmapSource Render(AnnotationDocument doc, ExportOptions options)
    {
        var sampler = new ImageSampler(doc.Image);
        var labels = doc.Annotations.ToDictionary(a => a, a => LabelRenderer.Layout(a, 1.0, sampler.Luminance));
        var composition = NotesComposition.Build(doc, options.Layout, a => labels[a], 1.0);
        var bounds = composition.Bounds;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            composition.DrawUnder(dc);
            dc.DrawImage(doc.Image, composition.ImageRect);
            composition.DrawOver(dc);
            foreach (var a in doc.Annotations) AnnotationRenderer.DrawShape(dc, a);
            foreach (var layout in labels.Values) if (layout != null) LabelRenderer.Draw(dc, layout);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Small preview of the annotated image for the library.</summary>
    public static BitmapSource RenderThumbnail(AnnotationDocument doc, BitmapSource? cachedBase, out BitmapSource baseImage, int maxWidth = 400, int maxHeight = 260)
    {
        double scale = Math.Min(1, Math.Min((double)maxWidth / doc.PixelWidth, (double)maxHeight / doc.PixelHeight));
        int w = Math.Max(1, (int)Math.Round(doc.PixelWidth * scale));
        int h = Math.Max(1, (int)Math.Round(doc.PixelHeight * scale));

        if (cachedBase == null || cachedBase.PixelWidth != w || cachedBase.PixelHeight != h)
        {
            var baseVisual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(baseVisual, BitmapScalingMode.HighQuality);
            using (var dc = baseVisual.RenderOpen()) dc.DrawImage(doc.Image, new Rect(0, 0, w, h));
            var rendered = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(baseVisual);
            rendered.Freeze();
            cachedBase = rendered;
        }
        baseImage = cachedBase;

        var sampler = new ImageSampler(doc.Image);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(cachedBase, new Rect(0, 0, w, h));
            dc.PushTransform(new ScaleTransform(scale, scale));
            foreach (var a in doc.Annotations) AnnotationRenderer.DrawShape(dc, a);
            foreach (var a in doc.Annotations)
            {
                var layout = LabelRenderer.Layout(a, 1.0, sampler.Luminance);
                if (layout != null) LabelRenderer.Draw(dc, layout);
            }
            dc.Pop();
        }
        var thumb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        thumb.Render(visual);
        thumb.Freeze();
        return thumb;
    }

    public static void SaveImage(BitmapSource bitmap, string path)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        BitmapSource frame = encoder is PngBitmapEncoder ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        encoder.Frames.Add(BitmapFrame.Create(frame));
        string tmp = path + ".tmp";
        using (var stream = File.Create(tmp)) encoder.Save(stream);
        FileUtil.ReplaceWith(tmp, path);
    }

    public static string NotesAsMarkdown(AnnotationDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {doc.Title}");
        sb.AppendLine($"_{doc.CapturedAt.ToString("f", CultureInfo.CurrentCulture)}_");
        sb.AppendLine();
        foreach (var a in NoteItems(doc))
        {
            string id = a.HasNumber ? a.NumberText : "•";
            string tag = a.Tag.Trim();
            string head = tag.Length > 0 ? $"**{tag}**" : "";
            string comment = a.Comment.Trim().Replace("\r\n", "\n").Replace("\n", "\n   ");
            string body = string.Join(" — ", new[] { head, comment }.Where(x => x.Length > 0));
            sb.AppendLine($"- **{id}** {body}".TrimEnd());
        }
        return sb.ToString();
    }

    public static List<Annotation> NoteItems(AnnotationDocument doc) => NotesComposition.NoteItems(doc);
}
