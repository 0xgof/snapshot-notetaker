namespace SnapshotNotetaker.Model;

public enum ShapeKind { Rectangle, Square, Ellipse, Circle, Spline }

public enum StrokeDash { Solid, Dashed, Dotted }

/// <summary>What the label attached to a shape shows.</summary>
public enum LabelMode { None, Number, Tag, NumberAndTag }

/// <summary>Visual form of the label.</summary>
public enum LabelShape { Box, Circle, CornerTab, Callout, Halo }

/// <summary>Where on the shape the label attaches.</summary>
public enum LabelAnchor { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Center }

/// <summary>How the label sits relative to its anchor point.</summary>
public enum LabelPlacement { Outside, OnEdge, Inside }

/// <summary>How label colors are chosen so the label stays readable.</summary>
public enum LabelColorScheme { MatchShape, Adaptive, Light, Dark, Custom }

public enum NumberFormat { Decimal, UpperAlpha, LowerAlpha, UpperRoman, LowerRoman }

/// <summary>How tags and comments are laid out when the snapshot is expanded for handover.</summary>
public enum NotesLayout { None, Margins, Right, Bottom }
