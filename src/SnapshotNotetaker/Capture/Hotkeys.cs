using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using SnapshotNotetaker.Interop;

namespace SnapshotNotetaker.Capture;

/// <summary>Actions that can be bound to a global shortcut.</summary>
public enum HotkeyAction { CaptureRegion, CaptureWindow, CaptureScreen, CaptureAllScreens, ShowApp }

/// <summary>A key plus modifiers, e.g. "Ctrl+Shift+PrintScreen". Empty means "not assigned".</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static readonly Hotkey None = new(ModifierKeys.None, Key.None);

    public bool IsEmpty => Key == Key.None;

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static string KeyName(Key key) => key switch
    {
        Key.Snapshot => "PrintScreen",
        Key.D0 or Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 or Key.D6 or Key.D7 or Key.D8 or Key.D9 => ((int)(key - Key.D0)).ToString(),
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        Key.OemPlus => "Plus",
        Key.OemMinus => "Minus",
        _ => key.ToString(),
    };

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var mods = ModifierKeys.None;
        var key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                case "printscreen" or "prtsc" or "prtscn": key = Key.Snapshot; break;
                case "pagedown": key = Key.Next; break;
                case "pageup": key = Key.Prior; break;
                case "plus": key = Key.OemPlus; break;
                case "minus": key = Key.OemMinus; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (Enum.TryParse<Key>(raw, true, out var k)) key = k;
                    else return None;
                    break;
            }
        }
        return new Hotkey(mods, key);
    }

    /// <summary>Shortcuts must use a modifier unless they are PrintScreen, Pause or a function key.</summary>
    public bool IsValidGlobal => !IsEmpty && (Modifiers != ModifierKeys.None || Key is Key.Snapshot or Key.Pause or Key.Scroll || (Key >= Key.F1 && Key <= Key.F24));
}

/// <summary>Registers system-wide shortcuts on a message-only window.</summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 0x5100;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("SnapshotNotetaker.Hotkeys")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0,
            ParentWindow = Native.HWND_MESSAGE,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a shortcut. Returns an error message, or null on success.</summary>
    public string? Register(Hotkey hotkey, Action handler)
    {
        if (hotkey.IsEmpty) return null;
        if (!hotkey.IsValidGlobal) return "Add Ctrl, Alt, Shift or Win to this key.";
        int id = _nextId++;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        if (!Native.RegisterHotKey(_source.Handle, id, (uint)hotkey.Modifiers | Native.MOD_NOREPEAT, vk))
        {
            int error = Marshal.GetLastWin32Error();
            return error == 1409 ? "Already used by another app." : $"Windows refused it (error {error}).";
        }
        _handlers[id] = handler;
        return null;
    }

    /// <summary>Checks whether Windows would accept the shortcut right now, without keeping it.</summary>
    public string? Test(Hotkey hotkey)
    {
        if (hotkey.IsEmpty) return null;
        if (!hotkey.IsValidGlobal) return "Add Ctrl, Alt, Shift or Win to this key.";
        int id = _nextId++;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        if (!Native.RegisterHotKey(_source.Handle, id, (uint)hotkey.Modifiers | Native.MOD_NOREPEAT, vk))
        {
            int error = Marshal.GetLastWin32Error();
            return error == 1409 ? "Already used by another app." : $"Windows refused it (error {error}).";
        }
        Native.UnregisterHotKey(_source.Handle, id);
        return null;
    }

    public void UnregisterAll()
    {
        foreach (int id in _handlers.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            _source.Dispatcher.BeginInvoke(handler);
        }
        return IntPtr.Zero;
    }
}
