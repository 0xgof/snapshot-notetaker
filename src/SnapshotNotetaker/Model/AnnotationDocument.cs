using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapshotNotetaker.Model;

/// <summary>A snapshot image plus its annotations. Numbers are recomputed whenever the list changes.</summary>
public sealed class AnnotationDocument : ObservableObject
{
    private NumberingOptions _numbering = new();
    private string _title;
    private bool _isDirty;
    private bool _renumbering;

    public AnnotationDocument(string id, BitmapSource image, double captureScale, string source, DateTime capturedAt, string? title = null)
    {
        Id = id;
        Image = NormalizeImage(image);
        CaptureScale = captureScale > 0 ? captureScale : 1;
        Source = source;
        CapturedAt = capturedAt;
        _title = string.IsNullOrWhiteSpace(title) ? source : title;
        Annotations.CollectionChanged += OnAnnotationsChanged;
    }

    /// <summary>Library id (folder name).</summary>
    public string Id { get; }
    public BitmapSource Image { get; }
    public int PixelWidth => Image.PixelWidth;
    public int PixelHeight => Image.PixelHeight;

    /// <summary>DPI scale of the screen the snapshot came from; default sizes are multiplied by it.</summary>
    public double CaptureScale { get; }
    public string Source { get; }
    public DateTime CapturedAt { get; }

    public string Title
    {
        get => _title;
        set { if (SetField(ref _title, value ?? "")) IsDirty = true; }
    }

    public ObservableCollection<Annotation> Annotations { get; } = new();

    public NumberingOptions Numbering
    {
        get => _numbering;
        set
        {
            if (!SetField(ref _numbering, value ?? new NumberingOptions())) return;
            Renumber();
            IsDirty = true;
        }
    }

    /// <summary>True when there are changes the library has not persisted yet.</summary>
    public bool IsDirty { get => _isDirty; set => SetField(ref _isDirty, value); }

    public int NotedCount => Annotations.Count(a => a.LabelMode != LabelMode.None || a.Comment.Length > 0);

    public void Renumber()
    {
        if (_renumbering) return;
        _renumbering = true;
        try
        {
            int n = Numbering.Start;
            foreach (var a in Annotations)
            {
                if (a.HasNumber)
                {
                    a.Number = n;
                    a.NumberText = Numbering.FormatNumber(n);
                    n++;
                }
                else
                {
                    a.Number = 0;
                    a.NumberText = "";
                }
            }
        }
        finally
        {
            _renumbering = false;
        }
    }

    public DocumentState CaptureState() => new(Annotations.Select(a => a.ToData()).ToArray(), Numbering);

    /// <summary>Restores a state while keeping annotation object identity (by id) so views stay stable.</summary>
    public void RestoreState(DocumentState state)
    {
        var existing = Annotations.ToDictionary(a => a.Id);
        var target = new List<Annotation>(state.Items.Length);
        foreach (var d in state.Items)
        {
            if (existing.TryGetValue(d.Id, out var a)) a.Apply(d);
            else a = Annotation.FromData(d);
            target.Add(a);
        }

        for (int i = 0; i < target.Count; i++)
        {
            if (i < Annotations.Count && ReferenceEquals(Annotations[i], target[i])) continue;
            int index = Annotations.IndexOf(target[i]);
            if (index >= 0) Annotations.Move(index, i);
            else Annotations.Insert(i, target[i]);
        }
        while (Annotations.Count > target.Count) Annotations.RemoveAt(Annotations.Count - 1);

        _numbering = state.Numbering;
        OnPropertyChanged(nameof(Numbering));
        Renumber();
        IsDirty = true;
    }

    private void OnAnnotationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (Annotation a in e.OldItems) a.PropertyChanged -= OnAnnotationPropertyChanged;
        if (e.NewItems != null)
            foreach (Annotation a in e.NewItems) a.PropertyChanged += OnAnnotationPropertyChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset)
            foreach (var a in Annotations) { a.PropertyChanged -= OnAnnotationPropertyChanged; a.PropertyChanged += OnAnnotationPropertyChanged; }

        Renumber();
        IsDirty = true;
    }

    private void OnAnnotationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Annotation.LabelMode):
                Renumber();
                IsDirty = true;
                break;
            case nameof(Annotation.Number):
            case nameof(Annotation.NumberText):
            case nameof(Annotation.DisplayText):
            case nameof(Annotation.HasNumber):
            case nameof(Annotation.Geometry):
                break;
            default:
                IsDirty = true;
                break;
        }
    }

    /// <summary>Converts any decoded image to a 96-DPI, 32-bit frozen bitmap (1 image pixel = 1 DIP).</summary>
    public static BitmapSource NormalizeImage(BitmapSource source)
    {
        bool fourBytes = source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32;
        if (fourBytes && Math.Abs(source.DpiX - 96) < 0.01 && Math.Abs(source.DpiY - 96) < 0.01)
        {
            if (!source.IsFrozen && source.CanFreeze) source.Freeze();
            return source;
        }

        BitmapSource converted = fourBytes ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight, stride = width * 4;
        var buffer = new byte[stride * height];
        converted.CopyPixels(buffer, stride, 0);
        var result = BitmapSource.Create(width, height, 96, 96, converted.Format, null, buffer, stride);
        result.Freeze();
        return result;
    }
}

public sealed record DocumentState(AnnotationData[] Items, NumberingOptions Numbering)
{
    public bool SameContent(DocumentState other)
        => Numbering == other.Numbering && Items.SequenceEqual(other.Items);
}
