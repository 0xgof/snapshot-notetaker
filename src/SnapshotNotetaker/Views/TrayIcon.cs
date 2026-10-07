using System.Windows;
using SnapshotNotetaker.Capture;
using Forms = System.Windows.Forms;

namespace SnapshotNotetaker.Views;

/// <summary>Notification-area icon: quick capture menu and a way back to the window.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly Dictionary<HotkeyAction, Forms.ToolStripMenuItem> _items = new();

    public TrayIcon(App app)
    {
        _app = app;
        Forms.Application.EnableVisualStyles(); // first WinForms use in the process: modern menu look
        var menu = new Forms.ContextMenuStrip();
        AddAction(menu, HotkeyAction.CaptureRegion, "Capture region");
        AddAction(menu, HotkeyAction.CaptureWindow, "Capture window");
        AddAction(menu, HotkeyAction.CaptureScreen, "Capture display");
        AddAction(menu, HotkeyAction.CaptureAllScreens, "Capture all displays");
        menu.Items.Add(new Forms.ToolStripSeparator());
        var open = new Forms.ToolStripMenuItem("Open Snapshot Notetaker", null, (_, _) => app.ShowMainWindow()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) };
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripMenuItem("Settings…", null, (_, _) => app.OpenSettings()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => app.ExitApp()));

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Snapshot Notetaker",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) app.ShowMainWindow();
        };
        Refresh();
    }

    public void Refresh()
    {
        foreach (var (action, item) in _items)
            item.ShortcutKeyDisplayString = _app.Settings.GetHotkey(action).ToString();
    }

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void AddAction(Forms.ContextMenuStrip menu, HotkeyAction action, string text)
    {
        var item = new Forms.ToolStripMenuItem(text, null, (_, _) => _app.RunAction(action));
        _items[action] = item;
        menu.Items.Add(item);
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
