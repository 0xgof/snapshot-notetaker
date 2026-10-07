using System.Windows;
using System.Windows.Media;
using SnapshotNotetaker.Rendering;

namespace SnapshotNotetaker.Model;

/// <summary>
/// One marked area on the snapshot. All coordinates and sizes are in image pixels.
/// Rect-like kinds use <see cref="Bounds"/>; splines use <see cref="Points"/> (the curve passes through them).
/// </summary>
public sealed class Annotation : ObservableObject
{
    private ShapeKind _kind;
    private Rect _bounds;
    private Point[] _points = Array.Empty<Point>();
    private bool _isClosed = true;
    private Color _strokeColor = Color.FromRgb(0xE5, 0x39, 0x35);
    private double _strokeWidth = 3;
    private StrokeDash _dash;
    private double _fillOpacity;
    private bool _strokeOutline;
    private LabelMode _labelMode = LabelMode.Number;
    private string _tag = "";
    private LabelStyle _label = new();
    private Vector _labelOffset;
    private string _comment = "";
    private int _number;
    private string _numberText = "";
    private Geometry? _geometry;

    public Guid Id { get; private set; } = Guid.NewGuid();

    public ShapeKind Kind
    {
        get => _kind;
        set { if (SetField(ref _kind, value)) InvalidateGeometry(); }
    }

    public Rect Bounds
    {
        get => _bounds;
        set { if (SetField(ref _bounds, value)) InvalidateGeometry(); }
    }

    /// <summary>Spline control points. Treated as immutable: assign a new array to change it.</summary>
    public Point[] Points
    {
        get => _points;
        set { if (SetField(ref _points, value ?? Array.Empty<Point>())) InvalidateGeometry(); }
    }

    public bool IsClosed
    {
        get => _isClosed;
        set { if (SetField(ref _isClosed, value)) InvalidateGeometry(); }
    }

    public Color StrokeColor { get => _strokeColor; set => SetField(ref _strokeColor, value); }
    public double StrokeWidth { get => _strokeWidth; set => SetField(ref _strokeWidth, value); }
    public StrokeDash Dash { get => _dash; set => SetField(ref _dash, value); }
    public double FillOpacity { get => _fillOpacity; set => SetField(ref _fillOpacity, value); }
    public bool StrokeOutline { get => _strokeOutline; set => SetField(ref _strokeOutline, value); }

    public LabelMode LabelMode
    {
        get => _labelMode;
        set
        {
            if (!SetField(ref _labelMode, value)) return;
            OnPropertyChanged(nameof(HasNumber));
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    public string Tag
    {
        get => _tag;
        set { if (SetField(ref _tag, value ?? "")) OnPropertyChanged(nameof(DisplayText)); }
    }

    public LabelStyle Label { get => _label; set => SetField(ref _label, value ?? new LabelStyle()); }
    public Vector LabelOffset { get => _labelOffset; set => SetField(ref _labelOffset, value); }
    public string Comment { get => _comment; set => SetField(ref _comment, value ?? ""); }

    /// <summary>Sequential number assigned by the document (0 when the label has no number).</summary>
    public int Number { get => _number; internal set => SetField(ref _number, value); }

    public string NumberText
    {
        get => _numberText;
        internal set { if (SetField(ref _numberText, value)) OnPropertyChanged(nameof(DisplayText)); }
    }

    public bool HasNumber => LabelMode is LabelMode.Number or LabelMode.NumberAndTag;
    public bool KeepsAspect => Kind is ShapeKind.Square or ShapeKind.Circle;
    public bool IsSpline => Kind == ShapeKind.Spline;

    public string DisplayText
    {
        get
        {
            string tag = Tag.Trim();
            return LabelMode switch
            {
                LabelMode.Number => NumberText,
                LabelMode.Tag => tag,
                LabelMode.NumberAndTag when tag.Length == 0 => NumberText,
                LabelMode.NumberAndTag when NumberText.Length == 0 => tag,
                LabelMode.NumberAndTag => $"{NumberText} · {tag}",
                _ => "",
            };
        }
    }

    public Geometry Geometry => _geometry ??= ShapeGeometry.Build(this);

    public Rect ShapeBounds => IsSpline ? (Points.Length == 0 ? Rect.Empty : Geometry.Bounds) : Bounds;

    public void Offset(Vector delta)
    {
        if (IsSpline) Points = Points.Select(p => p + delta).ToArray();
        else Bounds = Rect.Offset(Bounds, delta);
    }

    private void InvalidateGeometry()
    {
        _geometry = null;
        OnPropertyChanged(nameof(Geometry));
    }

    public AnnotationData ToData() => new()
    {
        Id = Id,
        Kind = Kind,
        Bounds = Bounds,
        Points = Points,
        IsClosed = IsClosed,
        StrokeColor = StrokeColor,
        StrokeWidth = StrokeWidth,
        Dash = Dash,
        FillOpacity = FillOpacity,
        StrokeOutline = StrokeOutline,
        LabelMode = LabelMode,
        Tag = Tag,
        Label = Label,
        LabelOffset = LabelOffset,
        Comment = Comment,
    };

    public void Apply(AnnotationData d)
    {
        Kind = d.Kind;
        Bounds = d.Bounds;
        Points = d.Points ?? Array.Empty<Point>();
        IsClosed = d.IsClosed;
        StrokeColor = d.StrokeColor;
        StrokeWidth = d.StrokeWidth;
        Dash = d.Dash;
        FillOpacity = d.FillOpacity;
        StrokeOutline = d.StrokeOutline;
        LabelMode = d.LabelMode;
        Tag = d.Tag ?? "";
        Label = d.Label ?? new LabelStyle();
        LabelOffset = d.LabelOffset;
        Comment = d.Comment ?? "";
    }

    public static Annotation FromData(AnnotationData d)
    {
        var a = new Annotation { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id };
        a.Apply(d);
        return a;
    }

    public Annotation Duplicate(Vector offset)
    {
        var copy = FromData(ToData());
        copy.Id = Guid.NewGuid();
        copy.Offset(offset);
        return copy;
    }
}

/// <summary>Immutable snapshot of an annotation, used for undo and persistence.</summary>
public sealed record AnnotationData
{
    public Guid Id { get; init; }
    public ShapeKind Kind { get; init; }
    public Rect Bounds { get; init; }
    public Point[] Points { get; init; } = Array.Empty<Point>();
    public bool IsClosed { get; init; } = true;
    public Color StrokeColor { get; init; }
    public double StrokeWidth { get; init; }
    public StrokeDash Dash { get; init; }
    public double FillOpacity { get; init; }
    public bool StrokeOutline { get; init; }
    public LabelMode LabelMode { get; init; }
    public string Tag { get; init; } = "";
    public LabelStyle Label { get; init; } = new();
    public Vector LabelOffset { get; init; }
    public string Comment { get; init; } = "";
}
