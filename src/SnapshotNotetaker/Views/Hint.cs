using System.Windows;

namespace SnapshotNotetaker.Views;

/// <summary>Placeholder text shown by the TextBox template while the box is empty.</summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Hint), new FrameworkPropertyMetadata(""));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
}
