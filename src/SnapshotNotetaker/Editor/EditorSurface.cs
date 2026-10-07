using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SnapshotNotetaker.Model;
using SnapshotNotetaker.Rendering;

namespace SnapshotNotetaker.Editor;

public enum EditorTool { Select, Rectangle, Square, Ellipse, Circle, Spline }

/// <summary>
/// The editing viewport: zoom/pan, tools, selection, handles and all pointer interaction.
/// Hosts an <see cref="AnnotationCanvas"/> under a single matrix transform, so zooming and panning are GPU-only.
/// </summary>
public sealed class EditorSurface : FrameworkElement
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(EditorSurface),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(EditorSurface),
        new FrameworkPropertyMetadata(Color.FromRgb(0x25, 0x63, 0xEB), (d, _) => ((EditorSurface)d).OnAccentChanged()));

    private static readonly double[] ZoomSteps =
        { 0.05, 0.1, 0.125, 0.167, 0.25, 0.333, 0.5, 0.667, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32 };

    private const double DragThreshold = 3;     // DIPs
    private const double HandleSize = 9;        // DIPs
    private const double HitTolerance = 5;      // DIPs

    private enum DragKind { None, Pending, Pan, Create, Resize, MovePoint, Move, Label, RubberBand, Freehand }

    private enum Handle { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

    private readonly AnnotationCanvas _canvas = new();
    private readonly MatrixTransform _transform = new();
    private readonly List<Annotation> _selection = new();

    private AnnotationDocument? _doc;
    private UndoManager? _undo;
    private EditorTool _tool = EditorTool.Rectangle;
    private double _zoom = 1; // device pixels per image pixel
    private Vector _offset;
    private bool _fitMode = true;
    private bool _spaceDown;
    private int _selectedPoint = -1;
    private bool _renderingOverlay;

    private SolidColorBrush _accentBrush = null!;
    private SolidColorBrush _accentSoftBrush = null!;

    // Drag state
    private DragKind _drag;
    private Point _downSurface, _downImage, _lastSurface;
    private DocumentState? _before;
    private bool _changed;
    private Annotation? _active;
    private Handle _handle;
    private Rect _startRect;
    private Vector _startLabelOffset;
    private List<(Annotation Annotation, Rect Bounds, Point[] Points)>? _moveStart;
    private Rect _rubber;
    private List<Point>? _freehand;

    // Click-by-click spline in progress (its last point follows the cursor)
    private Annotation? _spline;
    private bool _splineNearStart;

    public EditorSurface()
    {
        _canvas.RenderTransform = _transform;
        _canvas.IsHitTestVisible = false;
        AddVisualChild(_canvas);
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        _canvas.Rendered += (_, _) => RenderOverlay();
        _canvas.ViewBoundsChanged += (_, _) => OnViewBoundsChanged();
        OnAccentChanged();
    }

    /// <summary>Expanded view: shows the notes around the image exactly as Copy/Export will produce them.</summary>
    public NotesLayout NotesLayout
    {
        get => _canvas.NotesLayout;
        set
        {
            if (_canvas.NotesLayout == value) return;
            _fitMode = true;
            _canvas.NotesLayout = value;
            OnViewBoundsChanged();
        }
    }

    private bool _viewBoundsPending;

    private void OnViewBoundsChanged()
    {
        // Never move the picture under the mouse mid-gesture; catch up when the drag ends.
        if (_drag != DragKind.None)
        {
            _viewBoundsPending = true;
            InvalidateVisual();
            return;
        }
        _viewBoundsPending = false;
        if (_fitMode) Fit(RenderSize);
        UpdateView();
    }

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>Creates a new annotation with the current default style.</summary>
    public Func<ShapeKind, Annotation>? AnnotationFactory { get; set; }

    public event EventHandler? SelectionChanged;
    public event EventHandler? ViewChanged;
    public event EventHandler? ToolChanged;
    public event EventHandler<Point?>? PointerMoved;
    public event EventHandler<Annotation>? EditNotesRequested;

    public IReadOnlyList<Annotation> Selection => _selection;
    public AnnotationDocument? Document => _doc;
    public double Zoom => _zoom;
    public bool HasPendingSpline => _spline != null;

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            FinishSpline(commit: true);
            _tool = value;
            Cursor = DefaultCursor;
            ToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Cursor DefaultCursor => _tool == EditorTool.Select ? Cursors.Arrow : Cursors.Cross;

    /// <summary>DIPs per image pixel.</summary>
    private double Scale => _zoom / DpiScale;

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    // ------------------------------------------------------------------ document

    public void Attach(AnnotationDocument? document, UndoManager? undo)
    {
        if (_drag != DragKind.None) { _drag = DragKind.None; ReleaseMouseCapture(); }
        if (_doc != null) _doc.Annotations.CollectionChanged -= OnAnnotationsChanged;
        _spline = null;
        _freehand = null;
        _selection.Clear();
        _selectedPoint = -1;

        _doc = document;
        _undo = undo;
        _canvas.Document = document;
        if (document != null) document.Annotations.CollectionChanged += OnAnnotationsChanged;

        _fitMode = true;
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
        RenderOverlay();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnAnnotationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_doc == null) return;
        if (_spline != null && !_doc.Annotations.Contains(_spline)) _spline = null;
        if (_selection.RemoveAll(a => !_doc.Annotations.Contains(a)) > 0)
        {
            _selectedPoint = -1;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        RenderOverlay();
    }

    // ------------------------------------------------------------------ selection

    public void SelectOnly(Annotation a) => SetSelection(new[] { a });

    public void ClearSelection() => SetSelection(Array.Empty<Annotation>());

    public void SelectAll()
    {
        if (_doc != null) SetSelection(_doc.Annotations);
    }

    public void SetSelection(IEnumerable<Annotation> items)
    {
        var list = items.Where(a => _doc?.Annotations.Contains(a) == true).Distinct().ToList();
        if (list.SequenceEqual(_selection)) return;
        _selection.Clear();
        _selection.AddRange(list);
        _selectedPoint = -1;
        RenderOverlay();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteSelection()
    {
        if (_doc == null || _undo == null || _selection.Count == 0) return;

        // A selected spline point is deleted on its own.
        if (_selection.Count == 1 && _selection[0].IsSpline && _selectedPoint >= 0)
        {
            var a = _selection[0];
            int min = a.IsClosed ? 3 : 2;
            if (a.Points.Length > min && _selectedPoint < a.Points.Length)
            {
                _undo.Record(() =>
                {
                    var list = a.Points.ToList();
                    list.RemoveAt(_selectedPoint);
                    a.Points = list.ToArray();
                });
                _selectedPoint = -1;
                RenderOverlay();
                return;
            }
        }

        var toRemove = _selection.ToList();
        _undo.Record(() => { foreach (var a in toRemove) _doc.Annotations.Remove(a); });
    }

    public void DuplicateSelection()
    {
        if (_doc == null || _undo == null || _selection.Count == 0) return;
        var copies = new List<Annotation>();
        double offset = 16 / Math.Max(Scale, 0.01);
        _undo.Record(() =>
        {
            foreach (var a in _selection)
            {
                var copy = a.Duplicate(new Vector(offset, offset));
                _doc.Annotations.Add(copy);
                copies.Add(copy);
            }
        });
        SetSelection(copies);
    }

    public void Nudge(Vector delta)
    {
        if (_undo == null || _selection.Count == 0) return;
        _undo.Record(() => { foreach (var a in _selection) a.Offset(delta); });
    }

    /// <summary>Escape: cancel in-progress spline, else clear the selection, else go back to the select tool.</summary>
    public void Escape()
    {
        if (_spline != null) FinishSpline(commit: false);
        else if (_selection.Count > 0) ClearSelection();
        else Tool = EditorTool.Select;
    }

    public void FinishPendingSpline() => FinishSpline(commit: true);

    public void SetSpacePan(bool down)
    {
        _spaceDown = down;
        if (_drag == DragKind.None) Cursor = down ? Cursors.SizeAll : DefaultCursor;
    }

    /// <summary>Pans so the annotation is visible (used when picking a note in the sidebar).</summary>
    public void BringIntoView(Annotation a)
    {
        if (_doc == null) return;
        var b = a.ShapeBounds;
        if (b.IsEmpty) return;
        var onScreen = new Rect(b.X * Scale + _offset.X, b.Y * Scale + _offset.Y, b.Width * Scale, b.Height * Scale);
        var viewport = new Rect(RenderSize);
        if (viewport.Contains(onScreen)) return;
        _offset += new Vector(viewport.Width / 2 - (onScreen.X + onScreen.Width / 2), viewport.Height / 2 - (onScreen.Y + onScreen.Height / 2));
        _fitMode = false;
        UpdateView();
    }

    // ------------------------------------------------------------------ zoom & layout

    public void ZoomIn() => SetZoom(ZoomSteps.FirstOrDefault(z => z > _zoom * 1.001, ZoomSteps[^1]));
    public void ZoomOut() => SetZoom(ZoomSteps.LastOrDefault(z => z < _zoom * 0.999, ZoomSteps[0]));
    public void ZoomActual() => SetZoom(1);

    public void ZoomToFit()
    {
        _fitMode = true;
        Fit(RenderSize);
        UpdateView();
    }

    public void SetZoom(double zoom, Point? pivot = null)
    {
        if (_doc == null) return;
        zoom = Math.Clamp(zoom, 0.02, 32);
        var p = pivot ?? new Point(ActualWidth / 2, ActualHeight / 2);
        double oldScale = Scale;
        var imagePoint = new Point((p.X - _offset.X) / oldScale, (p.Y - _offset.Y) / oldScale);
        _zoom = zoom;
        _fitMode = false;
        _offset = new Vector(p.X - imagePoint.X * Scale, p.Y - imagePoint.Y * Scale);
        UpdateView();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _canvas;

    protected override Size MeasureOverride(Size availableSize)
    {
        _canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
                        double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _canvas.Arrange(new Rect(_canvas.DesiredSize));
        if (_fitMode) Fit(finalSize);
        else ClampOffset(finalSize);
        ApplyTransform();
        ViewChanged?.Invoke(this, EventArgs.Empty);
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (_doc == null) return;
        var b = _canvas.ViewBounds;
        var r = new Rect(_offset.X + b.X * Scale, _offset.Y + b.Y * Scale, b.Width * Scale, b.Height * Scale);
        for (int i = 1; i <= 4; i++)
        {
            var shadow = r;
            shadow.Inflate(i, i);
            shadow.Offset(0, i * 0.6);
            dc.DrawRectangle(null, new Pen(ColorUtil.Brush(Color.FromArgb((byte)(34 - i * 7), 0, 0, 0)), 1), shadow);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateArrange();
    }

    private void Fit(Size viewport)
    {
        var b = _canvas.ViewBounds;
        if (_doc == null || b.IsEmpty || viewport.Width <= 0 || viewport.Height <= 0) return;
        const double margin = 24;
        double sx = (viewport.Width - margin * 2) / b.Width;
        double sy = (viewport.Height - margin * 2) / b.Height;
        double dips = Math.Max(0.02 / DpiScale, Math.Min(Math.Min(sx, sy), 1.0 / DpiScale)); // never upscale past 100%
        _zoom = dips * DpiScale;
        _offset = new Vector((viewport.Width - b.Width * Scale) / 2 - b.X * Scale, (viewport.Height - b.Height * Scale) / 2 - b.Y * Scale);
    }

    private void ClampOffset(Size viewport)
    {
        var b = _canvas.ViewBounds;
        if (_doc == null || b.IsEmpty) return;
        const double margin = 48;
        double w = b.Width * Scale, h = b.Height * Scale;
        double left = _offset.X + b.X * Scale, top = _offset.Y + b.Y * Scale;
        left = w <= viewport.Width - margin ? (viewport.Width - w) / 2 : Math.Clamp(left, viewport.Width - w - margin, margin);
        top = h <= viewport.Height - margin ? (viewport.Height - h) / 2 : Math.Clamp(top, viewport.Height - h - margin, margin);
        _offset = new Vector(left - b.X * Scale, top - b.Y * Scale);
    }

    private void ApplyTransform()
    {
        double s = Scale, dpi = DpiScale;
        double x = Math.Round(_offset.X * dpi) / dpi, y = Math.Round(_offset.Y * dpi) / dpi; // device-pixel aligned
        _transform.Matrix = new Matrix(s, 0, 0, s, x, y);
        _canvas.SetImageScaling(_zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private void UpdateView()
    {
        ClampOffset(RenderSize);
        ApplyTransform();
        InvalidateVisual();
        RenderOverlay();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_doc == null) return;
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) != 0) SetZoom(_zoom * Math.Pow(1.0015, e.Delta), e.GetPosition(this));
        else
        {
            _fitMode = false;
            _offset += (mods & ModifierKeys.Shift) != 0 ? new Vector(e.Delta * 0.6, 0) : new Vector(0, e.Delta * 0.6);
            UpdateView();
        }
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_doc == null) return;

        var sp = e.GetPosition(this);
        var ip = e.GetPosition(_canvas);

        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _spaceDown))
        {
            BeginDrag(DragKind.Pan, sp, ip);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            if (_spline != null) { FinishSpline(commit: true); e.Handled = true; }
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;

        if (_spline != null)
        {
            SplineClick(ip, e.ClickCount);
            return;
        }

        if (e.ClickCount == 2 && HandleDoubleClick(ip)) return;

        // 1) Handles of the single selected annotation (in any tool).
        if (_selection.Count == 1 && HitHandle(_selection[0], ip, out var handle, out int pointIndex))
        {
            _active = _selection[0];
            if (pointIndex >= 0)
            {
                _selectedPoint = pointIndex;
                BeginDrag(DragKind.MovePoint, sp, ip);
            }
            else
            {
                _handle = handle;
                _startRect = _active.Bounds;
                BeginDrag(DragKind.Resize, sp, ip);
            }
            RenderOverlay();
            return;
        }

        // 2) Labels can be dragged in any tool.
        var labelHit = HitLabel(ip);
        if (labelHit != null)
        {
            if (!_selection.Contains(labelHit)) SelectOnly(labelHit);
            _active = labelHit;
            _startLabelOffset = labelHit.LabelOffset;
            BeginDrag(DragKind.Label, sp, ip);
            return;
        }

        // 3) In the expanded view, clicking a comment card selects its area.
        if (_canvas.HitNote(ip) is { } noteHit)
        {
            SelectOnly(noteHit);
            return;
        }

        if (_tool == EditorTool.Select)
        {
            bool additive = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;
            var hit = HitShape(ip);
            if (hit != null)
            {
                if (additive)
                {
                    if (_selection.Contains(hit))
                    {
                        SetSelection(_selection.Where(a => a != hit).ToList());
                        return;
                    }
                    SetSelection(_selection.Append(hit).ToList());
                }
                else if (!_selection.Contains(hit)) SelectOnly(hit);

                _moveStart = _selection.Select(a => (a, a.Bounds, a.Points)).ToList();
                BeginDrag(DragKind.Move, sp, ip);
                return;
            }

            if (!additive) ClearSelection();
            _rubber = new Rect(ip, ip);
            BeginDrag(DragKind.RubberBand, sp, ip);
            return;
        }

        // Drawing tools: wait to see whether this is a click or a drag.
        BeginDrag(DragKind.Pending, sp, ip);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_doc == null) return;
        var sp = e.GetPosition(this);
        var ip = e.GetPosition(_canvas);
        PointerMoved?.Invoke(this, ip);
        var mods = Keyboard.Modifiers;
        bool shift = (mods & ModifierKeys.Shift) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;

        switch (_drag)
        {
            case DragKind.None:
                if (_spline != null) UpdateSplineRubber(ip);
                UpdateHoverCursor(ip);
                break;

            case DragKind.Pan:
                _fitMode = false;
                _offset += sp - _lastSurface;
                UpdateView();
                break;

            case DragKind.Pending:
                if ((sp - _downSurface).Length >= DragThreshold)
                {
                    if (_tool == EditorTool.Spline)
                    {
                        _drag = DragKind.Freehand;
                        _freehand = new List<Point> { _downImage, ip };
                        RenderOverlay();
                    }
                    else StartCreate(ip, shift, alt);
                }
                break;

            case DragKind.Create when _active != null:
                _active.Bounds = CreateRect(_downImage, ip, _active.KeepsAspect || shift, alt);
                break;

            case DragKind.Resize when _active != null:
                _active.Bounds = ResizeRect(_startRect, _handle, ip, _active.KeepsAspect || shift);
                _changed = true;
                break;

            case DragKind.MovePoint when _active != null && _selectedPoint >= 0 && _selectedPoint < _active.Points.Length:
            {
                var pts = (Point[])_active.Points.Clone();
                pts[_selectedPoint] = ip;
                _active.Points = pts;
                _changed = true;
                break;
            }

            case DragKind.Move when _moveStart != null:
            {
                var delta = ip - _downImage;
                if (shift) delta = Math.Abs(delta.X) >= Math.Abs(delta.Y) ? new Vector(delta.X, 0) : new Vector(0, delta.Y);
                foreach (var (a, bounds, points) in _moveStart)
                {
                    if (a.IsSpline) a.Points = points.Select(p => p + delta).ToArray();
                    else a.Bounds = Rect.Offset(bounds, delta);
                }
                _changed = delta.LengthSquared > 0;
                break;
            }

            case DragKind.Label when _active != null:
                _active.LabelOffset = _startLabelOffset + (ip - _downImage);
                _changed = true;
                break;

            case DragKind.RubberBand:
                _rubber = new Rect(_downImage, ip);
                RenderOverlay();
                break;

            case DragKind.Freehand when _freehand != null:
                if ((ip - _freehand[^1]).Length * Scale >= 2)
                {
                    _freehand.Add(ip);
                    RenderOverlay();
                }
                break;
        }
        _lastSurface = sp;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        PointerMoved?.Invoke(this, null);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragKind.None) return;
        bool panButton = e.ChangedButton == MouseButton.Middle || (_drag == DragKind.Pan && e.ChangedButton == MouseButton.Left);
        if (_drag == DragKind.Pan ? !panButton : e.ChangedButton != MouseButton.Left) return;
        EndDrag(e.GetPosition(_canvas));
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag != DragKind.None) EndDrag(Mouse.GetPosition(_canvas), captureLost: true);
    }

    private void BeginDrag(DragKind kind, Point surfacePoint, Point imagePoint)
    {
        _drag = kind;
        _downSurface = _lastSurface = surfacePoint;
        _downImage = imagePoint;
        _changed = false;
        _before = kind is DragKind.Resize or DragKind.MovePoint or DragKind.Move or DragKind.Label ? _doc?.CaptureState() : null;
        CaptureMouse();
    }

    private void EndDrag(Point ip, bool captureLost = false)
    {
        var kind = _drag;
        _drag = DragKind.None;
        if (!captureLost) ReleaseMouseCapture();

        switch (kind)
        {
            case DragKind.Pending when !captureLost:
                if (_tool == EditorTool.Spline) StartClickSpline(ip);
                else
                {
                    var hit = HitShape(ip);
                    if (hit != null) SelectOnly(hit);
                    else ClearSelection();
                }
                break;

            case DragKind.Create when _active != null && _doc != null:
                if (_active.Bounds.Width * Scale < 3 || _active.Bounds.Height * Scale < 3)
                {
                    _doc.Annotations.Remove(_active);
                    _before = null;
                }
                else Commit();
                break;

            case DragKind.Freehand:
                FinishFreehand();
                break;

            case DragKind.RubberBand when _doc != null:
                if (_rubber.Width > 0 || _rubber.Height > 0)
                {
                    var picked = _doc.Annotations.Where(a => a.ShapeBounds.IntersectsWith(_rubber));
                    SetSelection((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? _selection.Concat(picked).ToList() : picked.ToList());
                }
                break;

            case DragKind.Resize:
            case DragKind.MovePoint:
            case DragKind.Move:
            case DragKind.Label:
                if (_changed) Commit();
                break;
        }

        _active = null;
        _moveStart = null;
        _before = null;
        RenderOverlay();
        Cursor = _spaceDown ? Cursors.SizeAll : DefaultCursor;
        if (_viewBoundsPending) OnViewBoundsChanged();
    }

    private void Commit()
    {
        if (_before != null && _undo != null) _undo.Push(_before);
        _before = null;
    }

    private bool HandleDoubleClick(Point ip)
    {
        if (_doc == null || _undo == null) return false;

        // Double-click on a selected spline's curve inserts a control point.
        if (_selection.Count == 1 && _selection[0].IsSpline)
        {
            var a = _selection[0];
            var nearest = Spline.Nearest(a.Points, a.IsClosed, ip);
            if (nearest.Segment >= 0 && nearest.Distance * Scale <= HitTolerance + a.StrokeWidth * Scale / 2)
            {
                int insertAt = nearest.Segment + 1;
                _undo.Record(() =>
                {
                    var list = a.Points.ToList();
                    list.Insert(insertAt, nearest.Point);
                    a.Points = list.ToArray();
                });
                _selectedPoint = insertAt;
                RenderOverlay();
                return true;
            }
        }

        var hit = HitLabel(ip) ?? _canvas.HitNote(ip) ?? (_tool == EditorTool.Select ? HitShape(ip) : null);
        if (hit == null) return false;
        SelectOnly(hit);
        EditNotesRequested?.Invoke(this, hit);
        return true;
    }

    // ------------------------------------------------------------------ creation

    private ShapeKind KindForTool => _tool switch
    {
        EditorTool.Square => ShapeKind.Square,
        EditorTool.Ellipse => ShapeKind.Ellipse,
        EditorTool.Circle => ShapeKind.Circle,
        EditorTool.Spline => ShapeKind.Spline,
        _ => ShapeKind.Rectangle,
    };

    private void StartCreate(Point ip, bool shift, bool alt)
    {
        if (_doc == null || AnnotationFactory == null) return;
        _before = _doc.CaptureState();
        var a = AnnotationFactory(KindForTool);
        a.Bounds = CreateRect(_downImage, ip, a.KeepsAspect || shift, alt);
        _doc.Annotations.Add(a);
        SelectOnly(a);
        _active = a;
        _drag = DragKind.Create;
        _changed = true;
    }

    private static Rect CreateRect(Point anchor, Point p, bool square, bool fromCenter)
    {
        double dx = p.X - anchor.X, dy = p.Y - anchor.Y;
        if (square)
        {
            double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -side : side;
            dy = dy < 0 ? -side : side;
        }
        return fromCenter
            ? new Rect(new Point(anchor.X - dx, anchor.Y - dy), new Point(anchor.X + dx, anchor.Y + dy))
            : new Rect(anchor, new Point(anchor.X + dx, anchor.Y + dy));
    }

    private static Rect ResizeRect(Rect start, Handle h, Point p, bool keepAspect)
    {
        bool left = h is Handle.TopLeft or Handle.Left or Handle.BottomLeft;
        bool right = h is Handle.TopRight or Handle.Right or Handle.BottomRight;
        bool top = h is Handle.TopLeft or Handle.Top or Handle.TopRight;
        bool bottom = h is Handle.BottomLeft or Handle.Bottom or Handle.BottomRight;

        if (keepAspect && (left || right) && (top || bottom))
        {
            var anchor = new Point(left ? start.Right : start.Left, top ? start.Bottom : start.Top);
            return CreateRect(anchor, p, square: true, fromCenter: false);
        }

        double l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
        if (left) l = p.X;
        if (right) r = p.X;
        if (top) t = p.Y;
        if (bottom) b = p.Y;
        return new Rect(new Point(l, t), new Point(r, b));
    }

    private void StartClickSpline(Point ip)
    {
        if (_doc == null || AnnotationFactory == null) return;
        _before = _doc.CaptureState();
        var a = AnnotationFactory(ShapeKind.Spline);
        a.IsClosed = false;
        a.Points = new[] { ip, ip };
        _doc.Annotations.Add(a);
        _spline = a;
        SelectOnly(a);
        _splineBefore = _before;
        _before = null;
    }

    private DocumentState? _splineBefore;

    private void SplineClick(Point ip, int clickCount)
    {
        if (_spline == null) return;
        var pts = _spline.Points;
        int real = pts.Length - 1;
        if (clickCount >= 2)
        {
            FinishSpline(commit: true);
            return;
        }
        if (real >= 3 && IsNear(ip, pts[0]))
        {
            FinishSpline(commit: true, close: true);
            return;
        }
        var list = pts.ToList();
        list[^1] = ip;
        list.Add(ip);
        _spline.Points = list.ToArray();
    }

    private void UpdateSplineRubber(Point ip)
    {
        if (_spline == null) return;
        var pts = (Point[])_spline.Points.Clone();
        pts[^1] = ip;
        _splineNearStart = pts.Length - 1 >= 3 && IsNear(ip, pts[0]);
        _spline.Points = pts;
    }

    private void FinishSpline(bool commit, bool close = false)
    {
        if (_spline == null || _doc == null) return;
        var a = _spline;
        _spline = null;
        _splineNearStart = false;

        var pts = a.Points.ToList();
        if (pts.Count > 0) pts.RemoveAt(pts.Count - 1); // the rubber-band point
        for (int i = pts.Count - 1; i > 0; i--)
            if ((pts[i] - pts[i - 1]).Length * Scale < 1) pts.RemoveAt(i);

        if (!commit || pts.Count < 2)
        {
            _doc.Annotations.Remove(a);
        }
        else
        {
            a.Points = pts.ToArray();
            if (close && pts.Count >= 3) a.IsClosed = true;
            if (_splineBefore != null) _undo?.Push(_splineBefore);
        }
        _splineBefore = null;
        RenderOverlay();
    }

    private void FinishFreehand()
    {
        var raw = _freehand;
        _freehand = null;
        if (_doc == null || _undo == null || AnnotationFactory == null || raw == null || raw.Count < 3) return;

        var pts = Spline.Simplify(raw, 3.5 / Scale);
        double minX = raw.Min(p => p.X), maxX = raw.Max(p => p.X), minY = raw.Min(p => p.Y), maxY = raw.Max(p => p.Y);
        double diagonal = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        double closeDistance = Math.Max(16 / Scale, diagonal * 0.12);
        bool closed = (raw[^1] - raw[0]).Length <= closeDistance;
        if (closed && pts.Count > 3 && (pts[^1] - pts[0]).Length <= closeDistance) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 2) return;
        if (pts.Count < 3) closed = false;

        var before = _doc.CaptureState();
        var a = AnnotationFactory(ShapeKind.Spline);
        a.Points = pts.ToArray();
        a.IsClosed = closed;
        _doc.Annotations.Add(a);
        _undo.Push(before);
        SelectOnly(a);
    }

    private bool IsNear(Point a, Point b) => (a - b).Length * Scale <= 9;

    // ------------------------------------------------------------------ hit testing

    private IEnumerable<(Handle Handle, Point Point)> HandlesFor(Annotation a)
    {
        if (a.IsSpline) yield break;
        var r = a.Bounds;
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        yield return (Handle.TopLeft, r.TopLeft);
        yield return (Handle.TopRight, r.TopRight);
        yield return (Handle.BottomRight, r.BottomRight);
        yield return (Handle.BottomLeft, r.BottomLeft);
        if (a.KeepsAspect) yield break;
        yield return (Handle.Top, new Point(cx, r.Top));
        yield return (Handle.Right, new Point(r.Right, cy));
        yield return (Handle.Bottom, new Point(cx, r.Bottom));
        yield return (Handle.Left, new Point(r.Left, cy));
    }

    private bool HitHandle(Annotation a, Point ip, out Handle handle, out int pointIndex)
    {
        handle = Handle.None;
        pointIndex = -1;
        double tol = (HandleSize / 2 + 2) / Scale;
        if (a.IsSpline)
        {
            int count = a == _spline ? a.Points.Length - 1 : a.Points.Length;
            for (int i = count - 1; i >= 0; i--)
            {
                if ((a.Points[i] - ip).Length <= tol)
                {
                    pointIndex = i;
                    return true;
                }
            }
            return false;
        }
        foreach (var (h, p) in HandlesFor(a))
        {
            if (Math.Abs(p.X - ip.X) <= tol && Math.Abs(p.Y - ip.Y) <= tol)
            {
                handle = h;
                return true;
            }
        }
        return false;
    }

    private Annotation? HitLabel(Point ip)
    {
        if (_doc == null) return null;
        double tol = 2 / Scale;
        for (int i = _doc.Annotations.Count - 1; i >= 0; i--)
        {
            var a = _doc.Annotations[i];
            var layout = _canvas.GetLabelLayout(a);
            if (layout == null) continue;
            var box = layout.Box;
            box.Inflate(tol, tol);
            if (box.Contains(ip)) return a;
        }
        return null;
    }

    private Annotation? HitShape(Point ip)
    {
        if (_doc == null) return null;
        double tol = HitTolerance / Scale;
        for (int i = _doc.Annotations.Count - 1; i >= 0; i--)
        {
            var a = _doc.Annotations[i];
            if (a == _spline) continue;
            var pen = new Pen(Brushes.Black, a.StrokeWidth + tol * 2);
            if (a.Geometry.StrokeContains(pen, ip)) return a;
        }

        // Clicking inside a closed shape selects the smallest one containing the point.
        Annotation? best = null;
        double bestArea = double.MaxValue;
        foreach (var a in _doc.Annotations)
        {
            if (a == _spline || (a.IsSpline && !a.IsClosed)) continue;
            if (!a.Geometry.FillContains(ip)) continue;
            var b = a.ShapeBounds;
            double area = b.Width * b.Height;
            if (area < bestArea) { bestArea = area; best = a; }
        }
        return best;
    }

    private void UpdateHoverCursor(Point ip)
    {
        Cursor cursor = DefaultCursor;
        if (_spaceDown) cursor = Cursors.SizeAll;
        else if (_selection.Count == 1 && HitHandle(_selection[0], ip, out var h, out int pi))
            cursor = pi >= 0 ? Cursors.Hand : CursorFor(h);
        else if (_spline == null && HitLabel(ip) != null) cursor = Cursors.SizeAll;
        else if (_spline == null && _canvas.HitNote(ip) != null) cursor = Cursors.Hand;
        else if (_tool == EditorTool.Select && HitShape(ip) != null) cursor = Cursors.SizeAll;
        if (Cursor != cursor) Cursor = cursor;
    }

    private static Cursor CursorFor(Handle h) => h switch
    {
        Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
        Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
        Handle.Top or Handle.Bottom => Cursors.SizeNS,
        _ => Cursors.SizeWE,
    };

    // ------------------------------------------------------------------ overlay

    private void OnAccentChanged()
    {
        _accentBrush = ColorUtil.Brush(Accent);
        _accentSoftBrush = ColorUtil.Brush(ColorUtil.WithAlpha(Accent, 0x30));
        RenderOverlay();
    }

    public void RenderOverlay()
    {
        if (_renderingOverlay) return;
        _renderingOverlay = true;
        try
        {
            using var dc = _canvas.OpenOverlay();
            if (_doc == null) return;
            double px = 1 / Math.Max(Scale, 1e-6);

            var solid = new Pen(_accentBrush, 1.25 * px);
            var dashed = new Pen(_accentBrush, px) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
            var faint = new Pen(ColorUtil.Brush(ColorUtil.WithAlpha(Accent, 0x90)), px);

            foreach (var a in _selection)
            {
                var b = a.ShapeBounds;
                if (b.IsEmpty) continue;
                if (_selection.Count > 1 || a.IsSpline)
                {
                    var outline = b;
                    outline.Inflate(a.StrokeWidth / 2 + 3 * px, a.StrokeWidth / 2 + 3 * px);
                    dc.DrawRectangle(null, dashed, outline);
                }
                var layout = _canvas.GetLabelLayout(a);
                if (layout != null)
                {
                    var lb = layout.Box;
                    lb.Inflate(2 * px, 2 * px);
                    dc.DrawRectangle(null, dashed, lb);
                }
            }

            if (_selection.Count == 1)
            {
                var a = _selection[0];
                if (a.IsSpline)
                {
                    int count = a == _spline ? a.Points.Length - 1 : a.Points.Length;
                    if (a.Points.Length > 1)
                    {
                        var poly = new StreamGeometry();
                        using (var ctx = poly.Open())
                        {
                            ctx.BeginFigure(a.Points[0], false, a.IsClosed && a != _spline);
                            for (int i = 1; i < a.Points.Length; i++) ctx.LineTo(a.Points[i], true, false);
                        }
                        dc.DrawGeometry(null, faint, poly);
                    }
                    double r = HandleSize / 2 * px;
                    for (int i = 0; i < count; i++)
                    {
                        var fill = i == _selectedPoint ? _accentBrush : Brushes.White;
                        dc.DrawEllipse(fill, solid, a.Points[i], r, r);
                    }
                    if (a == _spline && _splineNearStart && count > 0)
                        dc.DrawEllipse(_accentSoftBrush, solid, a.Points[0], r * 2.2, r * 2.2);
                }
                else
                {
                    var b = a.Bounds;
                    var frame = b;
                    frame.Inflate(a.StrokeWidth / 2 + 2 * px, a.StrokeWidth / 2 + 2 * px);
                    if (a.Kind is ShapeKind.Ellipse or ShapeKind.Circle) dc.DrawRectangle(null, dashed, b);
                    double hs = HandleSize * px;
                    foreach (var (_, p) in HandlesFor(a))
                        dc.DrawRectangle(Brushes.White, solid, new Rect(p.X - hs / 2, p.Y - hs / 2, hs, hs));
                }
            }

            if (_drag == DragKind.RubberBand)
                dc.DrawRectangle(_accentSoftBrush, solid, _rubber);

            if (_freehand is { Count: > 1 })
            {
                var stroke = new StreamGeometry();
                using (var ctx = stroke.Open())
                {
                    ctx.BeginFigure(_freehand[0], false, false);
                    for (int i = 1; i < _freehand.Count; i++) ctx.LineTo(_freehand[i], true, true);
                }
                var preview = AnnotationFactory?.Invoke(ShapeKind.Spline);
                var pen = preview != null
                    ? AnnotationRenderer.CreatePen(preview.StrokeColor, preview.StrokeWidth, StrokeDash.Solid)
                    : new Pen(_accentBrush, 2 * px);
                dc.DrawGeometry(null, pen, stroke);
            }
        }
        finally
        {
            _renderingOverlay = false;
        }
    }
}
