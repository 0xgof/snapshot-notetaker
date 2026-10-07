using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using SnapshotNotetaker.Model;
using SnapshotNotetaker.Support;

namespace SnapshotNotetaker.IO;

/// <summary>One snapshot in the library, as shown in the gallery.</summary>
public sealed class SnapshotEntry : ObservableObject
{
    private string _title = "";
    private string _source = "";
    private DateTime _capturedAt;
    private DateTime _modifiedAt;
    private int _noteCount;
    private int _pixelWidth, _pixelHeight;
    private BitmapSource? _thumbnail;

    public SnapshotEntry(string id, string folder)
    {
        Id = id;
        Folder = folder;
    }

    public string Id { get; }
    public string Folder { get; private set; }
    public string ImagePath => Path.Combine(Folder, SnapshotLibrary.ImageFileName);
    public string DocumentPath => Path.Combine(Folder, SnapshotLibrary.DocumentFileName);
    public string ThumbnailPath => Path.Combine(Folder, SnapshotLibrary.ThumbnailFileName);

    public string Title { get => _title; private set { if (SetField(ref _title, value)) OnPropertyChanged(nameof(DisplayTitle)); } }
    public string Source { get => _source; private set => SetField(ref _source, value); }
    public DateTime CapturedAt { get => _capturedAt; private set { if (SetField(ref _capturedAt, value)) OnPropertyChanged(nameof(Subtitle)); } }
    public DateTime ModifiedAt { get => _modifiedAt; private set => SetField(ref _modifiedAt, value); }
    public int NoteCount { get => _noteCount; private set { if (SetField(ref _noteCount, value)) OnPropertyChanged(nameof(Subtitle)); } }
    public int PixelWidth { get => _pixelWidth; private set => SetField(ref _pixelWidth, value); }
    public int PixelHeight { get => _pixelHeight; private set => SetField(ref _pixelHeight, value); }
    public BitmapSource? Thumbnail { get => _thumbnail; set => SetField(ref _thumbnail, value); }
    public string SearchText { get; private set; } = "";

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled snapshot" : Title;

    public string Subtitle
    {
        get
        {
            var local = CapturedAt;
            string when = local.Date == DateTime.Today ? $"Today {local:t}"
                : local.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {local:t}"
                : local.ToString("g");
            return NoteCount == 0 ? when : $"{when}  ·  {NoteCount} note{(NoteCount == 1 ? "" : "s")}";
        }
    }

    internal void Update(SnapshotFile file)
    {
        Title = file.Title;
        Source = file.Source;
        CapturedAt = file.CapturedAt;
        ModifiedAt = file.ModifiedAt;
        NoteCount = file.NoteCount;
        PixelWidth = file.Width;
        PixelHeight = file.Height;
        SearchText = file.SearchText();
    }

    internal void Relocate(string folder) => Folder = folder;
}

/// <summary>
/// Folder-based snapshot store. Each snapshot is a folder holding the untouched capture (image.png, written once),
/// its annotations (document.json) and a rendered preview (thumb.png). Edits only rewrite the small files,
/// so autosave stays cheap. All disk writes run sequentially on a background queue.
/// </summary>
public sealed class SnapshotLibrary
{
    public const string ImageFileName = "image.png";
    public const string DocumentFileName = "document.json";
    public const string ThumbnailFileName = "thumb.png";

    private readonly object _writeGate = new();
    private readonly ConditionalWeakTable<AnnotationDocument, BitmapSource> _thumbnailBases = new();
    private Task _writes = Task.CompletedTask;

    public SnapshotLibrary(string root) => Root = root;

    public string Root { get; private set; }

    /// <summary>Newest first.</summary>
    public ObservableCollection<SnapshotEntry> Entries { get; } = new();

    public SnapshotEntry? Find(string? id) => id == null ? null : Entries.FirstOrDefault(e => e.Id == id);

    public async Task LoadAsync()
    {
        string root = Root;
        var entries = await Task.Run(() => Scan(root));
        Entries.Clear();
        foreach (var e in entries.OrderByDescending(e => e.CapturedAt)) Entries.Add(e);
        _ = LoadThumbnailsAsync(Entries.ToList());
    }

    public (SnapshotEntry Entry, AnnotationDocument Document) Create(BitmapSource image, double scale, string source, DateTime capturedAt, SnapshotFile? content = null)
    {
        Directory.CreateDirectory(Root);
        string id = $"{capturedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        string folder = Path.Combine(Root, id);
        Directory.CreateDirectory(folder);

        var doc = new AnnotationDocument(id, image, scale, source, capturedAt, content?.Title);
        content?.ApplyTo(doc);
        var entry = new SnapshotEntry(id, folder);
        entry.Update(SnapshotFile.From(doc));
        Entries.Insert(0, entry);

        var normalized = doc.Image;
        Log.Info("library", $"New snapshot {id}: {normalized.PixelWidth}×{normalized.PixelHeight}{(content != null ? $", {content.Annotations.Count} areas imported" : "")}.");
        Enqueue(() =>
        {
            string tmp = Path.Combine(folder, ImageFileName + ".tmp");
            using (var stream = File.Create(tmp)) ProjectFile.EncodePng(normalized, stream);
            FileUtil.ReplaceWith(tmp, Path.Combine(folder, ImageFileName));
        });
        Save(doc);
        return (entry, doc);
    }

    /// <summary>Persists annotations and refreshes the preview. Must be called on the UI thread.</summary>
    public void Save(AnnotationDocument doc)
    {
        var entry = Find(doc.Id);
        if (entry == null) return; // deleted meanwhile

        var file = SnapshotFile.From(doc);
        string json = JsonSerializer.Serialize(file, Json.Options);
        _thumbnailBases.TryGetValue(doc, out var cachedBase);
        var thumbnail = Exporter.RenderThumbnail(doc, cachedBase, out var thumbnailBase);
        _thumbnailBases.AddOrUpdate(doc, thumbnailBase);

        entry.Update(file);
        entry.Thumbnail = thumbnail;
        doc.IsDirty = false;

        string folder = entry.Folder;
        Log.Debug("library", $"Saving {doc.Id}: {file.Annotations.Count} areas.");
        Enqueue(() =>
        {
            WriteAtomic(Path.Combine(folder, DocumentFileName), s => { using var w = new StreamWriter(s); w.Write(json); });
            WriteAtomic(Path.Combine(folder, ThumbnailFileName), s => ProjectFile.EncodePng(thumbnail, s));
        });
    }

    public async Task<AnnotationDocument> OpenAsync(SnapshotEntry entry)
    {
        await PendingWrites();
        var (image, file) = await Task.Run(() =>
        {
            var bitmap = LoadBitmap(entry.ImagePath) ?? throw new FileNotFoundException("The snapshot image is missing.", entry.ImagePath);
            var normalized = AnnotationDocument.NormalizeImage(bitmap);
            var data = JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(entry.DocumentPath), Json.Options) ?? new SnapshotFile();
            return (normalized, data);
        });
        var doc = new AnnotationDocument(entry.Id, image, file.CaptureScale, file.Source, file.CapturedAt, file.Title);
        file.ApplyTo(doc);
        return doc;
    }

    public async Task<(SnapshotEntry Entry, AnnotationDocument Document)> ImportAsync(string path)
    {
        if (string.Equals(Path.GetExtension(path), ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            var (image, file) = await Task.Run(() => ProjectFile.Read(path));
            return Create(image, file.CaptureScale, file.Source, file.CapturedAt == default ? DateTime.Now : file.CapturedAt, file);
        }

        var bitmap = await Task.Run(() => AnnotationDocument.NormalizeImage(LoadBitmap(path) ?? throw new FileNotFoundException("Image not found.", path)));
        return Create(bitmap, 1, Path.GetFileName(path), DateTime.Now);
    }

    public void Delete(SnapshotEntry entry)
    {
        Log.Info("library", $"Deleting snapshot {entry.Id} (to the Recycle Bin).");
        Entries.Remove(entry);
        string folder = entry.Folder;
        Enqueue(() =>
        {
            if (!Directory.Exists(folder)) return;
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folder,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        });
    }

    /// <summary>Points the library at another folder, optionally moving the existing snapshots there.</summary>
    public async Task ChangeRootAsync(string newRoot, bool moveExisting)
    {
        await PendingWrites();
        string oldRoot = Root;
        Log.Info("library", $"Changing library folder (move existing: {moveExisting}).");
        Directory.CreateDirectory(newRoot);
        if (moveExisting && !PathsEqual(oldRoot, newRoot) && Directory.Exists(oldRoot))
        {
            await Task.Run(() =>
            {
                foreach (var dir in Directory.EnumerateDirectories(oldRoot))
                {
                    if (!File.Exists(Path.Combine(dir, DocumentFileName))) continue;
                    string target = Path.Combine(newRoot, Path.GetFileName(dir));
                    if (Directory.Exists(target)) continue;
                    if (PathsEqual(Path.GetPathRoot(dir)!, Path.GetPathRoot(target)!)) Directory.Move(dir, target);
                    else
                    {
                        CopyDirectory(dir, target);
                        Directory.Delete(dir, true);
                    }
                }
            });
        }
        Root = newRoot;
        await LoadAsync();
    }

    /// <summary>Blocks until queued writes finish (used on exit).</summary>
    public void Flush()
    {
        Task pending;
        lock (_writeGate) pending = _writes;
        pending.Wait(TimeSpan.FromSeconds(20));
    }

    private Task PendingWrites()
    {
        lock (_writeGate) return _writes;
    }

    private void Enqueue(Action write)
    {
        lock (_writeGate)
        {
            _writes = _writes.ContinueWith(_ =>
            {
                try { write(); }
                catch (Exception ex) { Log.Error("library", "Library write failed.", ex); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private static List<SnapshotEntry> Scan(string root)
    {
        var list = new List<SnapshotEntry>();
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            string docPath = Path.Combine(dir, DocumentFileName);
            if (!File.Exists(docPath) || !File.Exists(Path.Combine(dir, ImageFileName))) continue;
            try
            {
                var file = JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(docPath), Json.Options);
                if (file == null) continue;
                var entry = new SnapshotEntry(Path.GetFileName(dir), dir);
                entry.Update(file);
                list.Add(entry);
            }
            catch (Exception ex)
            {
                Log.Warn("library", $"Skipping unreadable snapshot {Path.GetFileName(dir)}.", ex);
            }
        }
        return list;
    }

    private static async Task LoadThumbnailsAsync(List<SnapshotEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Thumbnail != null) continue;
            var thumb = await Task.Run(() => LoadBitmap(entry.ThumbnailPath));
            if (thumb != null && entry.Thumbnail == null) entry.Thumbnail = thumb;
        }
    }

    public static BitmapSource? LoadBitmap(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            Log.Warn("library", $"Could not load an image ({Path.GetExtension(path)}).", ex);
            return null;
        }
    }

    private static void WriteAtomic(string path, Action<Stream> write)
    {
        string tmp = path + ".tmp";
        using (var stream = File.Create(tmp)) write(stream);
        FileUtil.ReplaceWith(tmp, path);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (var dir in Directory.EnumerateDirectories(source)) CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
