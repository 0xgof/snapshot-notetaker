using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SnapshotNotetaker.Capture;

namespace SnapshotNotetaker.Views;

/// <summary>Records a shortcut: focus it and press the key combination. Backspace/Delete clears it, Esc cancels.</summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(Hotkey), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(Capture.Hotkey.None, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).UpdateText()));

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        Hint.SetText(this, "Not set — click and press keys");
        UpdateText();
    }

    public event EventHandler? HotkeyChanged;

    public Hotkey Hotkey
    {
        get => (Hotkey)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Text = "";
        Hint.SetText(this, "Press a key combination…");
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Hint.SetText(this, "Not set — click and press keys");
        UpdateText();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Tab:
                e.Handled = false;
                return;
            case Key.Escape:
                MoveFocusAway();
                return;
            case Key.Back or Key.Delete when Keyboard.Modifiers == ModifierKeys.None:
                Commit(Capture.Hotkey.None);
                return;
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin:
                Text = new Hotkey(CurrentModifiers(), Key.None).ToString().TrimEnd('+') + "+…";
                return;
        }
        Commit(new Hotkey(CurrentModifiers(), key));
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        // Windows only delivers PrintScreen on key-up.
        if (e.Key == Key.Snapshot)
        {
            e.Handled = true;
            Commit(new Hotkey(CurrentModifiers(), Key.Snapshot));
        }
        else base.OnPreviewKeyUp(e);
    }

    private static ModifierKeys CurrentModifiers()
    {
        var mods = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
        return mods;
    }

    private void Commit(Hotkey hotkey)
    {
        Hotkey = hotkey;
        UpdateText();
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
        MoveFocusAway();
    }

    private void MoveFocusAway()
    {
        var scope = FocusManager.GetFocusScope(this);
        FocusManager.SetFocusedElement(scope, null);
        Keyboard.ClearFocus();
    }

    private void UpdateText() => Text = Hotkey.ToString();
}
