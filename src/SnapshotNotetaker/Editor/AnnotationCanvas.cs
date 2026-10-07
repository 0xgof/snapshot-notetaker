using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SnapshotNotetaker.Model;
using SnapshotNotetaker.Rendering;

namespace SnapshotNotetaker.Editor;

/// <summary>
/// Retained-mode visual host for a document: one visual for the image, one shape visual and one label visual
/// per annotation (labels live on a layer above all shapes so they stay readable), and an overlay visual for
/// selection chrome. Only annotations that changed are re-rendered, coalesced once per dispatcher frame.
/// Coordinates are image pixels; zoom/pan is applied by the parent as a render transform.
/// </summary>
public sealed class AnnotationCanvas : FrameworkElement
{
    private sealed class Entry
    {
        public readonly DrawingVisual Shape = new();
        public readonly DrawingVisual Label = new();
        public LabelLayout? Layout;
    }

    private readonly VisualCollection _children;
    private readonly DrawingVisual _notesUnder = new();
    private readonly DrawingVisual _imageVisual = new();
    private readonly DrawingVisual _notesOver = new();
    private readonly ContainerVisual _shapeLayer = new();
    private readonly ContainerVisual _labelLayer = new();
    private readonly DrawingVisual _overlay = new();
    private readonly Dictionary<Annotation, Entry> _entries = new();
    private readonly HashSet<Annotation> _dirty = new();
    private AnnotationDocument? _document;
    private ImageSampler? _sampler;
    private NotesLayout _notesLayout;
    private NotesComposition? _composition;
    private bool _flushQueued;
    private bool _notesDirty;

    public AnnotationCanvas()
    {
        _children = new VisualCollection(this) { _notesUnder, _imageVisual, _notesOver, _shapeLayer, _labelLayer, _overlay };
    }

    /// <summary>Raised after annotation visuals were re-rendered.</summary>
    public event EventHandler? Rendered;

    /// <summary>Raised when the expanded area (image + notes) changes size.</summary>
    public event EventHandler? ViewBoundsChanged;

    /// <summary>Shows the tags and comments around the image (as they will be copied/exported), or nothing.</summary>
    public NotesLayout NotesLayout
    {
        get => _notesLayout;
        set
        {
            if (_notesLayout == value) return;
            _notesLayout = value;
            RenderNotes();
        }
    }

    /// <summary>The visible picture in image coordinates: the image, or the image plus its notes when expanded.</summary>
    public Rect ViewBounds => _composition?.Bounds ?? (_document == null ? Rect.Empty : new Rect(0, 0, _document.PixelWidth, _document.PixelHeight));

    /// <summary>The annotation whose note card / list row is at this point (expanded view only).</summary>
    public Annotation? HitNote(Point p) => _composition?.HitTest(p);

    public AnnotationDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;
            if (_document != null)
            {
                _document.Annotations.CollectionChanged -= OnCollectionChanged;
                _document.PropertyChanged -= OnDocumentChanged;
            }
            foreach (var a in _entries.Keys) a.PropertyChanged -= OnAnnotationChanged;
            _entries.Clear();
            _dirty.Clear();
            _shapeLayer.Children.Clear();
            _labelLayer.Children.Clear();

            _document = value;
            _sampler = value == null ? null : new ImageSampler(value.Image);
            using (var dc = _imageVisual.RenderOpen())
            {
                if (value != null) dc.DrawImage(value.Image, new Rect(0, 0, value.PixelWidth, value.PixelHeight));
            }
            if (value != null)
            {
                value.Annotations.CollectionChanged += OnCollectionChanged;
                value.PropertyChanged += OnDocumentChanged;
                SyncEntries();
            }
            using (_overlay.RenderOpen()) { }
            RenderNotes();
            InvalidateMeasure();
        }
    }

    public void SetImageScaling(BitmapScalingMode mode)
    {
        if (RenderOptions.GetBitmapScalingMode(_imageVisual) != mode) RenderOptions.SetBitmapScalingMode(_imageVisual, mode);
    }

    public DrawingContext OpenOverlay() => _overlay.RenderOpen();

    /// <summary>Cached label layout from the last render (image coordinates).</summary>
    public LabelLayout? GetLabelLayout(Annotation a) => _entries.TryGetValue(a, out var e) ? e.Layout : null;

    public void FlushNow()
    {
        if (_dirty.Count == 0 && !_notesDirty) return;
        var pixelsPerDip = PixelsPerDip;
        foreach (var a in _dirty)
            if (_entries.TryGetValue(a, out var entry)) Render(a, entry, pixelsPerDip);
        _dirty.Clear();
        _notesDirty = false;
        RenderNotes();
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    private void RenderNotes()
    {
        var before = ViewBounds;
        if (_document == null || _notesLayout == NotesLayout.None)
        {
            if (_composition != null)
            {
                _composition = null;
                using (_notesUnder.RenderOpen()) { }
                using (_notesOver.RenderOpen()) { }
            }
        }
        else
        {
            _composition = NotesComposition.Build(_document, _notesLayout, GetLabelLayout, PixelsPerDip);
            using (var dc = _notesUnder.RenderOpen()) _composition.DrawUnder(dc);
            using (var dc = _notesOver.RenderOpen()) _composition.DrawOver(dc);
        }
        if (ViewBounds != before) ViewBoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AnnotationDocument.Title) or nameof(AnnotationDocument.Numbering) && _notesLayout != NotesLayout.None)
        {
            _notesDirty = true;
            QueueFlush();
        }
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size availableSize)
        => _document == null ? new Size() : new Size(_document.PixelWidth, _document.PixelHeight);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        foreach (var a in _entries.Keys) _dirty.Add(a);
        QueueFlush();
    }

    private double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncEntries();

    private void SyncEntries()
    {
        if (_document == null) return;
        var current = new HashSet<Annotation>(_document.Annotations);
        foreach (var gone in _entries.Keys.Where(a => !current.Contains(a)).ToList())
        {
            gone.PropertyChanged -= OnAnnotationChanged;
            _entries.Remove(gone);
            _dirty.Remove(gone);
        }

        var pixelsPerDip = PixelsPerDip;
        foreach (var a in _document.Annotations)
        {
            if (_entries.ContainsKey(a)) continue;
            var entry = new Entry();
            _entries[a] = entry;
            a.PropertyChanged += OnAnnotationChanged;
            Render(a, entry, pixelsPerDip);
        }

        // Rebuild layer order to follow document order (z-order = numbering order).
        _shapeLayer.Children.Clear();
        _labelLayer.Children.Clear();
        foreach (var a in _document.Annotations)
        {
            var entry = _entries[a];
            _shapeLayer.Children.Add(entry.Shape);
            _labelLayer.Children.Add(entry.Label);
        }
        if (_notesLayout != NotesLayout.None)
        {
            _notesDirty = true;
            QueueFlush();
        }
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    private void OnAnnotationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Annotation a) return;
        _dirty.Add(a);
        QueueFlush();
    }

    private void QueueFlush()
    {
        if (_flushQueued) return;
        _flushQueued = true;
        // Normal priority runs before the next render pass, so drags never lag a frame behind.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            _flushQueued = false;
            FlushNow();
        });
    }

    private void Render(Annotation a, Entry entry, double pixelsPerDip)
    {
        using (var dc = entry.Shape.RenderOpen()) AnnotationRenderer.DrawShape(dc, a);
        entry.Layout = LabelRenderer.Layout(a, pixelsPerDip, _sampler == null ? null : _sampler.Luminance);
        using (var dc = entry.Label.RenderOpen())
        {
            if (entry.Layout != null) LabelRenderer.Draw(dc, entry.Layout);
        }
    }
}
