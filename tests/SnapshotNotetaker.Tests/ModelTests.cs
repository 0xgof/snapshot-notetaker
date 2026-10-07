using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapshotNotetaker.Capture;
using SnapshotNotetaker.Model;
using Xunit;

namespace SnapshotNotetaker.Tests;

public class NumberingTests
{
    [Theory]
    [InlineData(1, NumberFormat.Decimal, "1")]
    [InlineData(27, NumberFormat.UpperAlpha, "AA")]
    [InlineData(26, NumberFormat.LowerAlpha, "z")]
    [InlineData(1994, NumberFormat.UpperRoman, "MCMXCIV")]
    [InlineData(4, NumberFormat.LowerRoman, "iv")]
    [InlineData(0, NumberFormat.UpperAlpha, "0")]
    public void Formats(int value, NumberFormat format, string expected)
        => Assert.Equal(expected, Numbering.Format(value, format));

    [Fact]
    public void PrefixAndSuffix()
        => Assert.Equal("#B)", new NumberingOptions { Format = NumberFormat.UpperAlpha, Prefix = "#", Suffix = ")" }.FormatNumber(2));
}

public class DocumentTests
{
    private static AnnotationDocument NewDoc()
    {
        var image = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgr32, null, new byte[64], 16);
        image.Freeze();
        return new AnnotationDocument("test", image, 1, "test", DateTime.Now);
    }

    private static Annotation Rect(LabelMode mode = LabelMode.Number)
        => new() { Kind = ShapeKind.Rectangle, Bounds = new Rect(0, 0, 10, 10), LabelMode = mode };

    [Fact]
    public void NumbersAreRecalculatedLive() => Sta.Run(() =>
    {
        var doc = NewDoc();
        var a = Rect();
        var b = Rect();
        var c = Rect();
        doc.Annotations.Add(a);
        doc.Annotations.Add(b);
        doc.Annotations.Add(c);
        Assert.Equal(new[] { "1", "2", "3" }, doc.Annotations.Select(x => x.NumberText));

        doc.Annotations.Remove(a);
        Assert.Equal("1", b.NumberText);
        Assert.Equal("2", c.NumberText);

        doc.Annotations.Move(1, 0);
        Assert.Equal("1", c.NumberText);
        Assert.Equal("2", b.NumberText);

        c.LabelMode = LabelMode.None; // unnumbered areas are skipped
        Assert.Equal("", c.NumberText);
        Assert.Equal("1", b.NumberText);

        doc.Numbering = new NumberingOptions { Format = NumberFormat.UpperAlpha, Start = 3 };
        Assert.Equal("C", b.NumberText);
    });

    [Fact]
    public void DisplayTextCombinesNumberAndTag() => Sta.Run(() =>
    {
        var doc = NewDoc();
        var a = Rect(LabelMode.NumberAndTag);
        doc.Annotations.Add(a);
        Assert.Equal("1", a.DisplayText);
        a.Tag = "Login";
        Assert.Equal("1 · Login", a.DisplayText);
        a.LabelMode = LabelMode.Tag;
        Assert.Equal("Login", a.DisplayText);
    });

    [Fact]
    public void UndoRedoRestoresStateAndKeepsIdentity() => Sta.Run(() =>
    {
        var doc = NewDoc();
        var undo = new UndoManager(doc);
        var a = Rect();
        undo.Record(() => doc.Annotations.Add(a));
        undo.Record(() => a.Bounds = new Rect(5, 5, 20, 20));
        undo.Record(() => doc.Annotations.Add(Rect()));
        Assert.Equal(2, doc.Annotations.Count);

        undo.Undo();
        Assert.Single(doc.Annotations);
        Assert.Same(a, doc.Annotations[0]);
        undo.Undo();
        Assert.Equal(new Rect(0, 0, 10, 10), a.Bounds);
        undo.Redo();
        Assert.Equal(new Rect(5, 5, 20, 20), a.Bounds);
        undo.Undo();
        undo.Undo();
        Assert.Empty(doc.Annotations);
        Assert.False(undo.CanUndo);
    });

    [Fact]
    public void RecordSkipsNoOpChanges() => Sta.Run(() =>
    {
        var doc = NewDoc();
        var undo = new UndoManager(doc);
        undo.Record(() => { });
        Assert.False(undo.CanUndo);
    });
}

public class HotkeyTests
{
    [Theory]
    [InlineData("PrintScreen")]
    [InlineData("Ctrl+PrintScreen")]
    [InlineData("Ctrl+Shift+S")]
    [InlineData("Alt+Win+F9")]
    [InlineData("Ctrl+5")]
    public void ParseFormatRoundTrip(string text) => Assert.Equal(text, Hotkey.Parse(text).ToString());

    [Fact]
    public void PlainLettersNeedAModifier()
    {
        Assert.False(Hotkey.Parse("S").IsValidGlobal);
        Assert.True(Hotkey.Parse("PrintScreen").IsValidGlobal);
        Assert.True(Hotkey.Parse("F8").IsValidGlobal);
        Assert.True(Hotkey.Parse("Ctrl+Q").IsValidGlobal);
        Assert.True(Hotkey.Parse("").IsEmpty);
        Assert.True(Hotkey.Parse("Ctrl+Nonsense").IsEmpty);
    }
}
