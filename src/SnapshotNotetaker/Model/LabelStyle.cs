using System.Windows.Media;

namespace SnapshotNotetaker.Model;

/// <summary>Immutable label appearance. Sizes are in image pixels.</summary>
public sealed record LabelStyle
{
    public LabelShape Shape { get; init; } = LabelShape.Box;
    public LabelAnchor Anchor { get; init; } = LabelAnchor.TopLeft;
    public LabelPlacement Placement { get; init; } = LabelPlacement.OnEdge;
    public LabelColorScheme Scheme { get; init; } = LabelColorScheme.MatchShape;
    public Color CustomFill { get; init; } = Color.FromRgb(0xFF, 0xD6, 0x00);
    public Color CustomText { get; init; } = Color.FromRgb(0x11, 0x11, 0x11);
    public double FontSize { get; init; } = 14;
    public bool Bold { get; init; } = true;
    public bool Outline { get; init; } = true;
    public bool Shadow { get; init; } = true;
}
