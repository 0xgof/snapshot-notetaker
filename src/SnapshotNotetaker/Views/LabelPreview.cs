using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using SnapshotNotetaker.Model;
using SnapshotNotetaker.Rendering;

namespace SnapshotNotetaker.Views;

/// <summary>Renders an annotation's number badge exactly as it appears on the image (used in the notes sidebar).</summary>
public sealed class LabelPreview : FrameworkElement
{
    public static readonly DependencyProperty AnnotationProperty = DependencyProperty.Register(
        nameof(Annotation), typeof(Annotation), typeof(LabelPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnAnnotationChanged));

    private Annotation? _subscribed;

    public LabelPreview()
    {
        Loaded += (_, _) => Subscribe(Annotation);
        Unloaded += (_, _) => Subscribe(null);
    }

    public Annotation? Annotation
    {
        get => (Annotation?)GetValue(AnnotationProperty);
        set => SetValue(AnnotationProperty, value);
    }

    private static void OnAnnotationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var preview = (LabelPreview)d;
        if (preview.IsLoaded) preview.Subscribe(e.NewValue as Annotation);
    }

    private void Subscribe(Annotation? annotation)
    {
        if (ReferenceEquals(_subscribed, annotation)) return;
        if (_subscribed != null) _subscribed.PropertyChanged -= OnPropertyChanged;
        _subscribed = annotation;
        if (_subscribed != null) _subscribed.PropertyChanged += OnPropertyChanged;
        InvalidateVisual();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Model.Annotation.Bounds):
            case nameof(Model.Annotation.Points):
            case nameof(Model.Annotation.Geometry):
            case nameof(Model.Annotation.LabelOffset):
            case nameof(Model.Annotation.Comment):
                return;
            default:
                InvalidateVisual();
                break;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var a = Annotation;
        if (a == null) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        string text = a.HasNumber ? a.NumberText : a.LabelMode == LabelMode.Tag ? Shorten(a.Tag.Trim(), 3) : "";

        if (text.Length > 0)
        {
            var layout = LabelRenderer.LayoutBadge(a, text, center, VisualTreeHelper.GetDpi(this).PixelsPerDip, 12);
            LabelRenderer.Draw(dc, layout);
            return;
        }

        // No label: a small outline of the shape in its color.
        var pen = new Pen(ColorUtil.Brush(a.StrokeColor), 2) { LineJoin = PenLineJoin.Round };
        var box = new Rect(center.X - 9, center.Y - 7, 18, 14);
        switch (a.Kind)
        {
            case ShapeKind.Ellipse:
                dc.DrawEllipse(null, pen, center, 9, 6.5);
                break;
            case ShapeKind.Circle:
                dc.DrawEllipse(null, pen, center, 7, 7);
                break;
            case ShapeKind.Spline:
                var g = Spline.Build(new[] { new Point(box.Left, box.Bottom), new Point(center.X - 2, box.Top), new Point(center.X + 3, box.Bottom - 2), new Point(box.Right, box.Top + 2) }, false);
                dc.DrawGeometry(null, pen, g);
                break;
            case ShapeKind.Square:
                dc.DrawRectangle(null, pen, new Rect(center.X - 7, center.Y - 7, 14, 14));
                break;
            default:
                dc.DrawRectangle(null, pen, box);
                break;
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
