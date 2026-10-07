using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SnapshotNotetaker.Rendering;

namespace SnapshotNotetaker.Views;

/// <summary>Swatch palette popup with a fallback to the system color dialog.</summary>
internal static class ColorPicker
{
    public static readonly Color[] Palette =
    {
        C("#E53935"), C("#FB8C00"), C("#FFC107"), C("#FDD835"), C("#7CB342"), C("#2E7D32"), C("#00897B"), C("#00ACC1"),
        C("#1E88E5"), C("#3949AB"), C("#8E24AA"), C("#D81B60"), C("#6D4C41"), C("#757575"), C("#111111"), C("#FFFFFF"),
    };

    public static void Show(FrameworkElement target, Color current, Action<Color> picked)
    {
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            VerticalOffset = 4,
        };

        var swatches = new WrapPanel { Width = 8 * 30 };
        var flat = (Style)Application.Current.FindResource("FlatButton");
        foreach (var color in Palette)
        {
            bool isCurrent = color.R == current.R && color.G == current.G && color.B == current.B;
            var chip = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(5),
                Background = ColorUtil.Brush(color),
                BorderThickness = new Thickness(isCurrent ? 2 : 1),
            };
            if (isCurrent) chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
            else chip.BorderBrush = ColorUtil.Brush(Color.FromArgb(0x50, 0x80, 0x80, 0x80));

            var button = new Button { Style = flat, Width = 28, Height = 28, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(1), Content = chip, ToolTip = ColorUtil.ToHex(color)[3..] };
            var c = color;
            button.Click += (_, _) =>
            {
                popup.IsOpen = false;
                picked(c);
            };
            swatches.Children.Add(button);
        }

        var more = new Button { Style = (Style)Application.Current.FindResource("LinkButton"), Content = "More colors…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        more.Click += (_, _) =>
        {
            popup.IsOpen = false;
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
                CustomColors = Palette.Select(p => p.R | (p.G << 8) | (p.B << 16)).ToArray(),
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                picked(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
        };

        var border = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { swatches, more } },
        };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Popup");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        popup.Child = border;
        popup.IsOpen = true;
    }

    private static Color C(string hex)
    {
        ColorUtil.TryParse(hex, out var c);
        return c;
    }
}
