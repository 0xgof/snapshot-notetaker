using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media.Imaging;
using SnapshotNotetaker.Model;

namespace SnapshotNotetaker.IO;

/// <summary>Serialized form of a snapshot's metadata and annotations (document.json).</summary>
public sealed class SnapshotFile
{
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public double CaptureScale { get; set; } = 1;
    public int Width { get; set; }
    public int Height { get; set; }
    public NumberingOptions Numbering { get; set; } = new();
    public List<AnnotationData> Annotations { get; set; } = new();

    public static SnapshotFile From(AnnotationDocument doc) => new()
    {
        Title = doc.Title,
        Source = doc.Source,
        CapturedAt = doc.CapturedAt,
        ModifiedAt = DateTime.Now,
        CaptureScale = doc.CaptureScale,
        Width = doc.PixelWidth,
        Height = doc.PixelHeight,
        Numbering = doc.Numbering,
        Annotations = doc.Annotations.Select(a => a.ToData()).ToList(),
    };

    /// <summary>Loads numbering and annotations into a freshly created document.</summary>
    public void ApplyTo(AnnotationDocument doc)
    {
        doc.Numbering = Numbering ?? new NumberingOptions();
        foreach (var data in Annotations ?? new List<AnnotationData>()) doc.Annotations.Add(Annotation.FromData(data));
        doc.IsDirty = false;
    }

    public string SearchText()
        => string.Join(" ", new[] { Title, Source }.Concat(Annotations.SelectMany(a => new[] { a.Tag, a.Comment }))).ToLowerInvariant();

    public int NoteCount => Annotations.Count(a => a.LabelMode != LabelMode.None || !string.IsNullOrWhiteSpace(a.Comment));
}

/// <summary>Single-file exchange format (.snapnote): a zip holding image.png and document.json.</summary>
public static class ProjectFile
{
    public const string Extension = ".snapnote";

    public static void Write(AnnotationDocument doc, string path)
    {
        var file = SnapshotFile.From(doc);
        var image = doc.Image;
        string tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var imageEntry = zip.CreateEntry("image.png", CompressionLevel.NoCompression);
            using (var s = imageEntry.Open()) EncodePng(image, s);
            var docEntry = zip.CreateEntry("document.json", CompressionLevel.Optimal);
            using (var s = docEntry.Open()) JsonSerializer.Serialize(s, file, Json.Options);
        }
        FileUtil.ReplaceWith(tmp, path);
    }

    public static (BitmapSource Image, SnapshotFile File) Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var docEntry = zip.GetEntry("document.json") ?? throw new InvalidDataException("document.json is missing.");
        var imageEntry = zip.GetEntry("image.png") ?? throw new InvalidDataException("image.png is missing.");

        SnapshotFile file;
        using (var s = docEntry.Open()) file = JsonSerializer.Deserialize<SnapshotFile>(s, Json.Options) ?? new SnapshotFile();

        using var buffer = new MemoryStream();
        using (var s = imageEntry.Open()) s.CopyTo(buffer);
        buffer.Position = 0;
        var frame = BitmapFrame.Create(buffer, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return (AnnotationDocument.NormalizeImage(frame), file);
    }

    public static void EncodePng(BitmapSource image, Stream stream)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        if (stream.CanSeek)
        {
            encoder.Save(stream);
            return;
        }
        // WIC needs a seekable stream (zip entries are not).
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }
}
