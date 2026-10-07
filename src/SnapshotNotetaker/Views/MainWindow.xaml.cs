using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SnapshotNotetaker.Capture;
using SnapshotNotetaker.Editor;
using SnapshotNotetaker.IO;
using SnapshotNotetaker.Model;
using SnapshotNotetaker.Rendering;
using SnapshotNotetaker.Settings;
using SnapshotNotetaker.Themes;
using SnapshotNotetaker.Support;

namespace SnapshotNotetaker.Views;

public partial class MainWindow : Window
{
    private const int DocumentCacheSize = 4;

    private static readonly double[] StrokeWidths = { 1, 2, 3, 4, 6, 8, 12 };
    private static readonly double[] FontSizes = { 10, 12, 14, 16, 18, 20, 24, 28, 32, 40, 48 };

    private readonly App _app;
    private readonly DispatcherTimer _autosave;
    private readonly Dictionary<string, UndoManager> _undoById = new();
    private readonly LinkedList<AnnotationDocument> _recentDocuments = new();
    private readonly ICollectionView _libraryView;

    private AnnotationDocument? _doc;
    private SnapshotEntry? _entry;
    private UndoManager? _undo;
    private DocumentState? _textEditBefore;
    private bool _syncing;
    private bool _suppressNotesSync;
    private bool _suppressLibrarySelection;
    private int _openVersion;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();

        _autosave = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        _autosave.Tick += (_, _) =>
        {
            if (Surface.IsMouseCaptured || Surface.HasPendingSpline) return; // keep waiting until the gesture ends
            SaveNow();
        };

        InitPropertyBar();

        Surface.AnnotationFactory = CreateAnnotation;
        Surface.SelectionChanged += Surface_SelectionChanged;
        Surface.ViewChanged += (_, _) => UpdateZoomText();
        Surface.ToolChanged += (_, _) => SyncToolButtons();
        Surface.PointerMoved += (_, p) => CursorText.Text = p is { } pt && _doc != null && pt.X >= 0 && pt.Y >= 0 && pt.X < _doc.PixelWidth && pt.Y < _doc.PixelHeight
            ? $"{(int)pt.X}, {(int)pt.Y}" : "";
        Surface.EditNotesRequested += (_, a) => FocusComment(a);

        _libraryView = CollectionViewSource.GetDefaultView(_app.Library.Entries);
        _libraryView.Filter = FilterEntry;
        LibraryList.ItemsSource = _libraryView;
        _app.Library.Entries.CollectionChanged += (_, _) => UpdateLibraryHeader();

        var settings = _app.Settings;
        Surface.Tool = settings.LastTool;
        SyncToolButtons();
        LibraryColumn.Width = new GridLength(Math.Clamp(settings.LibraryWidth, 160, 520));
        NotesColumn.Width = new GridLength(Math.Clamp(settings.NotesWidth, 220, 640));
        SetLibraryVisible(settings.ShowLibrary);
        SetNotesVisible(settings.ShowNotes);

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        // Icon-only toolbar on narrow windows (e.g. a portrait monitor); tooltips still name every button.
        SizeChanged += (_, _) =>
        {
            // Only touch the resource when it actually flips: every assignment makes WPF re-walk the whole tree.
            var labels = ActualWidth < 1400 ? Visibility.Collapsed : Visibility.Visible;
            if (Resources["ToolbarLabelVisibility"] is Visibility current && current == labels) return;
            Resources["ToolbarLabelVisibility"] = labels;
        };
        PreviewDragOver += OnPreviewDragOver;
        Drop += OnDrop;

        UpdateHotkeyHints();
        UpdateLibraryHeader();
        ApplyExpandState();
        ShowDocument(null, null);
    }

    private AppSettings Settings => _app.Settings;
    private SnapshotLibrary Library => _app.Library;

    /// <summary>Library folder of the open snapshot (support bundles can include it on request).</summary>
    public string? CurrentSnapshotFolder => _entry?.Folder;

    // =================================================================== notice bar

    private Action? _infoPrimary, _infoSecondary;

    /// <summary>Shows a dismissible notice under the toolbars (e.g. "closed unexpectedly last time").</summary>
    public void ShowInfoBar(string text, string primaryText, Action primary, string? secondaryText = null, Action? secondary = null)
    {
        InfoBarText.Text = text;
        InfoBarPrimary.Content = primaryText;
        _infoPrimary = primary;
        InfoBarSecondary.Content = secondaryText;
        InfoBarSecondary.Visibility = secondaryText == null ? Visibility.Collapsed : Visibility.Visible;
        _infoSecondary = secondary;
        InfoBar.Visibility = Visibility.Visible;
    }

    private void HideInfoBar()
    {
        InfoBar.Visibility = Visibility.Collapsed;
        _infoPrimary = _infoSecondary = null;
    }

    private void InfoBarPrimary_Click(object sender, RoutedEventArgs e)
    {
        var action = _infoPrimary;
        HideInfoBar();
        action?.Invoke();
    }

    private void InfoBarSecondary_Click(object sender, RoutedEventArgs e)
    {
        var action = _infoSecondary;
        HideInfoBar();
        action?.Invoke();
    }

    private void InfoBarClose_Click(object sender, RoutedEventArgs e) => HideInfoBar();

    private void HelpMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Help & support…", _app.OpenSupport));
        menu.Items.Add(MenuItem("Create support bundle…", () => _ = SupportActions.CreateBundleAsync(this)));
        menu.Items.Add(MenuItem("Copy diagnostic summary", () =>
            SetStatus(SupportActions.CopySummary() ? "Diagnostic summary copied." : "The clipboard is busy; try again.")));
        menu.Items.Add(MenuItem("Open log folder", SupportActions.OpenLogFolder));
        menu.Items.Add(new Separator());
        var version = MenuItem($"Snapshot Notetaker {BuildInfo.Version}", () => { });
        version.IsEnabled = false;
        menu.Items.Add(version);
        OpenMenu(menu, (FrameworkElement)sender);
    }

    // =================================================================== documents

    /// <summary>Adds a fresh capture to the library and opens it.</summary>
    public void AddCapture(CaptureResult capture)
    {
        try
        {
            var (entry, doc) = Library.Create(capture.Image, capture.DpiScale, capture.Source, DateTime.Now);
            if (SearchBox.Text.Length > 0) SearchBox.Text = "";
            OpenDocument(entry, doc);
            SetStatus($"Captured {capture.Image.PixelWidth} × {capture.Image.PixelHeight} — saved to the library.");
        }
        catch (Exception ex)
        {
            ShowError("Could not store the snapshot.", ex);
        }
    }

    public async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var (entry, doc) = await Library.ImportAsync(path);
                if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                OpenDocument(entry, doc);
                SetStatus($"Imported {Path.GetFileName(path)}.");
            }
            catch (Exception ex)
            {
                ShowError($"Could not import {Path.GetFileName(path)}.", ex, $"Could not import a {Path.GetExtension(path)} file.");
            }
        }
    }

    public async Task OpenEntryAsync(SnapshotEntry entry)
    {
        if (_entry == entry && _doc != null) return;
        SaveNow();
        int version = ++_openVersion;
        var doc = _recentDocuments.FirstOrDefault(d => d.Id == entry.Id);
        if (doc == null)
        {
            SetStatus("Opening…");
            using var timing = Log.Time("library", "Open snapshot", LogLevel.Debug);
            try
            {
                doc = await Library.OpenAsync(entry);
                timing.Detail = $"{doc.PixelWidth}×{doc.PixelHeight}, {doc.Annotations.Count} areas";
            }
            catch (Exception ex)
            {
                if (version == _openVersion) ShowError("Could not open this snapshot.", ex);
                return;
            }
            if (version != _openVersion) return; // another snapshot was picked meanwhile
            SetStatus("");
        }
        ShowDocument(doc, entry);
    }

    public async Task OpenLastSnapshotAsync()
    {
        var entry = Library.Find(Settings.LastSnapshotId) ?? Library.Entries.FirstOrDefault();
        if (entry != null) await OpenEntryAsync(entry);
    }

    /// <summary>Forgets cached documents/undo history (their library folder is about to change).</summary>
    public void ResetForLibraryChange()
    {
        SaveNow();
        _recentDocuments.Clear();
        _undoById.Clear();
        ShowDocument(null, null);
    }

    /// <summary>Selects an annotation by index (used by DevTools snapshots).</summary>
    internal void DevSelect(int index)
    {
        if (_doc != null && index < _doc.Annotations.Count) Surface.SelectOnly(_doc.Annotations[index]);
    }

    private void OpenDocument(SnapshotEntry entry, AnnotationDocument doc)
    {
        SaveNow();
        _openVersion++;
        ShowDocument(doc, entry);
    }

    private void ShowDocument(AnnotationDocument? doc, SnapshotEntry? entry)
    {
        if (_doc != null)
        {
            _doc.PropertyChanged -= Doc_PropertyChanged;
            _doc.Annotations.CollectionChanged -= Doc_AnnotationsChanged;
        }

        _doc = doc;
        _entry = entry;
        _textEditBefore = null;

        if (doc != null)
        {
            _recentDocuments.Remove(doc);
            _recentDocuments.AddFirst(doc);
            while (_recentDocuments.Count > DocumentCacheSize) _recentDocuments.RemoveLast();

            if (!_undoById.TryGetValue(doc.Id, out var undo))
            {
                undo = new UndoManager(doc);
                undo.Changed += (s, _) => { if (ReferenceEquals(s, _undo)) UpdateCommandStates(); };
                _undoById[doc.Id] = undo;
            }
            undo.Document = doc;
            _undo = undo;
            doc.PropertyChanged += Doc_PropertyChanged;
            doc.Annotations.CollectionChanged += Doc_AnnotationsChanged;
            Settings.LastSnapshotId = doc.Id;
        }
        else _undo = null;

        Surface.Attach(doc, _undo);
        NotesList.ItemsSource = doc?.Annotations;
        EmptyState.Visibility = doc == null ? Visibility.Visible : Visibility.Collapsed;
        PropertiesBar.IsEnabled = true;

        _syncing = true;
        TitleBox.Text = doc?.Title ?? "";
        _syncing = false;
        TitleBox.IsEnabled = doc != null;
        CapturedText.Text = doc == null ? "" : $"{doc.CapturedAt:g}  ·  {doc.PixelWidth} × {doc.PixelHeight}";
        ImageSizeText.Text = doc == null ? "" : $"{doc.PixelWidth} × {doc.PixelHeight} px";
        SaveStateText.Text = "";

        SyncNumberingControls();
        SyncProperties();
        UpdateNotesHeader();
        UpdateWindowTitle();
        UpdateCommandStates();
        UpdateZoomText();

        _suppressLibrarySelection = true;
        LibraryList.SelectedItem = entry;
        if (entry != null) LibraryList.ScrollIntoView(entry);
        _suppressLibrarySelection = false;

        if (doc != null) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Surface.Focus());
    }

    private void Doc_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_doc == null) return;
        switch (e.PropertyName)
        {
            case nameof(AnnotationDocument.IsDirty) when _doc.IsDirty:
                SaveStateText.Text = "Editing…";
                _autosave.Stop();
                _autosave.Start();
                break;
            case nameof(AnnotationDocument.Title):
                UpdateWindowTitle();
                break;
            case nameof(AnnotationDocument.Numbering):
                SyncNumberingControls();
                break;
        }
    }

    private void Doc_AnnotationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateNotesHeader();

    /// <summary>Writes pending edits of the open snapshot to the library (annotations + preview).</summary>
    public void SaveNow()
    {
        _autosave.Stop();
        if (_doc == null || !_doc.IsDirty) return;
        try
        {
            Library.Save(_doc);
            SaveStateText.Text = $"Saved {DateTime.Now:t}";
        }
        catch (Exception ex)
        {
            SaveStateText.Text = "Not saved";
            Log.Error("library", "Autosave failed.", ex);
        }
    }

    private Annotation CreateAnnotation(ShapeKind kind)
    {
        var d = Settings.Defaults;
        double s = _doc?.CaptureScale ?? 1;
        return new Annotation
        {
            Kind = kind,
            StrokeColor = d.StrokeColor,
            StrokeWidth = d.StrokeWidth * s,
            Dash = d.Dash,
            FillOpacity = d.FillOpacity,
            StrokeOutline = d.StrokeOutline,
            LabelMode = d.LabelMode,
            Label = d.Label with { FontSize = d.Label.FontSize * s },
            IsClosed = true,
        };
    }

    private double DocScale => _doc?.CaptureScale ?? 1;

    // =================================================================== library panel

    private bool FilterEntry(object item)
    {
        string query = SearchBox?.Text.Trim().ToLowerInvariant() ?? "";
        if (query.Length == 0 || item is not SnapshotEntry e) return true;
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term => e.SearchText.Contains(term));
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _suppressLibrarySelection = true;
        _libraryView.Refresh();
        LibraryList.SelectedItem = _entry;
        _suppressLibrarySelection = false;
        UpdateLibraryHeader();
    }

    private void UpdateLibraryHeader()
    {
        int total = Library.Entries.Count;
        int shown = LibraryList.Items.Count;
        LibraryCountText.Text = total == 0 ? "" : shown == total ? total.ToString() : $"{shown} of {total}";
        LibraryEmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyText.Text = total == 0 ? "Your snapshots will appear here.\nThey are saved automatically." : "No snapshot matches your search.";
    }

    private async void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLibrarySelection) return;
        if (LibraryList.SelectedItem is SnapshotEntry entry) await OpenEntryAsync(entry);
    }

    private void LibraryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && LibraryList.SelectedItem is SnapshotEntry entry)
        {
            DeleteSnapshot(entry);
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            BeginRename();
            e.Handled = true;
        }
    }

    private void DeleteSnapshot(SnapshotEntry entry)
    {
        var answer = MessageBox.Show(this, $"Move “{entry.DisplayTitle}” to the Recycle Bin?", "Delete snapshot",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        int index = Library.Entries.IndexOf(entry);
        bool wasOpen = entry == _entry;
        if (wasOpen) _autosave.Stop();
        Library.Delete(entry);
        _undoById.Remove(entry.Id);
        var cached = _recentDocuments.FirstOrDefault(d => d.Id == entry.Id);
        if (cached != null) _recentDocuments.Remove(cached);

        if (wasOpen)
        {
            var next = Library.Entries.Count == 0 ? null : Library.Entries[Math.Clamp(index, 0, Library.Entries.Count - 1)];
            if (next != null) _ = OpenEntryAsync(next);
            else ShowDocument(null, null);
        }
        SetStatus("Snapshot moved to the Recycle Bin.");
    }

    private void BeginRename()
    {
        if (_doc == null) return;
        if (!Settings.ShowNotes) SetNotesVisible(true);
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _doc == null) return;
        _doc.Title = TitleBox.Text;
    }

    private void OpenLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Library.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Library.Root}\"") { UseShellExecute = true });
    }

    private SnapshotEntry? ContextEntry => LibraryList.SelectedItem as SnapshotEntry;

    private void LibraryExport_Click(object sender, RoutedEventArgs e) => ExportImage();
    private void LibraryCopy_Click(object sender, RoutedEventArgs e) => CopyImage();
    private void LibraryExportSnapnote_Click(object sender, RoutedEventArgs e) => ExportSnapnote();
    private void LibraryRename_Click(object sender, RoutedEventArgs e) => BeginRename();

    private void LibraryShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ContextEntry is { } entry && Directory.Exists(entry.Folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.ImagePath}\"") { UseShellExecute = true });
    }

    private void LibraryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ContextEntry is { } entry) DeleteSnapshot(entry);
    }

    // =================================================================== capture

    private void CaptureRegion_Click(object sender, RoutedEventArgs e) => _app.RunAction(HotkeyAction.CaptureRegion);
    private void CaptureWindowPick_Click(object sender, RoutedEventArgs e) => _app.RunAction(HotkeyAction.CaptureWindow);
    private void CaptureScreen_Click(object sender, RoutedEventArgs e) => _app.RunAction(HotkeyAction.CaptureScreen);

    private void CaptureWindowMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Pick a window on screen…", () => _app.RunAction(HotkeyAction.CaptureWindow), Settings.GetHotkey(HotkeyAction.CaptureWindow).ToString()));
        var windows = WindowFinder.GetWindows().Where(w => w.IsAppWindow).Take(40).ToList();
        if (windows.Count > 0) menu.Items.Add(new Separator());
        foreach (var w in windows)
        {
            var window = w;
            menu.Items.Add(MenuItem(Truncate(w.Title, 70), () => _app.CaptureSpecificWindow(window)));
        }
        OpenMenu(menu, (FrameworkElement)sender);
    }

    private void CaptureScreenMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Display under the mouse", () => _app.RunAction(HotkeyAction.CaptureScreen), Settings.GetHotkey(HotkeyAction.CaptureScreen).ToString()));
        var monitors = ScreenInfo.GetMonitors();
        if (monitors.Count > 1)
        {
            menu.Items.Add(MenuItem("All displays", () => _app.RunAction(HotkeyAction.CaptureAllScreens), Settings.GetHotkey(HotkeyAction.CaptureAllScreens).ToString()));
            menu.Items.Add(new Separator());
            foreach (var m in monitors)
            {
                var monitor = m;
                menu.Items.Add(MenuItem(m.Description + (m.IsPrimary ? " — primary" : ""), () => _app.CaptureMonitor(monitor.DeviceName)));
            }
        }
        OpenMenu(menu, (FrameworkElement)sender);
    }

    private void UpdateHotkeyHints()
    {
        string Hk(HotkeyAction a) => Settings.GetHotkey(a) is { IsEmpty: false } h ? h.ToString() : "no shortcut";
        RegionButton.ToolTip = $"Capture a region ({Hk(HotkeyAction.CaptureRegion)})";
        WindowButton.ToolTip = $"Capture a window ({Hk(HotkeyAction.CaptureWindow)})";
        ScreenButton.ToolTip = $"Capture the full screen ({Hk(HotkeyAction.CaptureScreen)})";
        EmptyHint.Text = $"Region  {Hk(HotkeyAction.CaptureRegion)}     ·     Window  {Hk(HotkeyAction.CaptureWindow)}     ·     Full screen  {Hk(HotkeyAction.CaptureScreen)}\n"
                         + "Shortcuts work from anywhere while the app runs (also from the tray). Change them in Settings.";
    }

    public void OnSettingsChanged()
    {
        UpdateHotkeyHints();
        SyncProperties();
    }

    // =================================================================== import / export

    private void Import_Click(object sender, RoutedEventArgs e) => Import();

    private async void Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import into the library",
            Filter = "Images and snapshots|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*" + ProjectFile.Extension + "|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true) await ImportFilesAsync(dialog.FileNames);
    }

    private void ExportMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Export image…", ExportImage, "Ctrl+E"));
        menu.Items.Add(MenuItem("Copy image", CopyImage, "Ctrl+Shift+C"));
        menu.Items.Add(MenuItem("Copy notes as text", CopyNotes));
        menu.Items.Add(MenuItem("Export .snapnote file…", ExportSnapnote));
        foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = _doc != null;
        menu.Items.Add(new Separator());
        AddLayoutChoices(menu, includeImageOnly: true);
        OpenMenu(menu, (FrameworkElement)sender);
    }

    private ExportOptions CurrentExportOptions => new(Settings.EffectiveNotesLayout);

    // =================================================================== expanded (handover) view

    private static readonly (NotesLayout Layout, string Text)[] LayoutChoices =
    {
        (NotesLayout.Margins, "Comments beside each area"),
        (NotesLayout.Right, "Comment list on the right"),
        (NotesLayout.Bottom, "Comment list below"),
    };

    private void AddLayoutChoices(ContextMenu menu, bool includeImageOnly)
    {
        var current = Settings.EffectiveNotesLayout;
        if (includeImageOnly)
        {
            var only = MenuItem("Image only", () => SetExpanded(false));
            only.IsChecked = current == NotesLayout.None;
            menu.Items.Add(only);
        }
        foreach (var (layout, text) in LayoutChoices)
        {
            var choice = layout;
            var item = MenuItem(includeImageOnly ? $"Expanded — {char.ToLower(text[0])}{text[1..]}" : text, () => SetExpanded(true, choice));
            item.IsChecked = current == layout;
            menu.Items.Add(item);
        }
    }

    private void ExpandToggle_Click(object sender, RoutedEventArgs e) => SetExpanded(!Settings.ExpandWithNotes);

    private void ExpandLayout_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AddLayoutChoices(menu, includeImageOnly: false);
        OpenMenu(menu, ExpandToggle);
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyImage();

    /// <summary>Turns the expanded view on/off (optionally choosing its layout). Copy and Export follow what is shown.</summary>
    private void SetExpanded(bool expanded, NotesLayout? layout = null)
    {
        Settings.ExpandWithNotes = expanded;
        if (layout is { } l && l != NotesLayout.None) Settings.ExpandLayout = l;
        Log.Info("ui", $"Expanded view {(expanded ? $"on ({Settings.ExpandLayout})" : "off")}.");
        _app.SaveSettingsSoon();
        ApplyExpandState();
        if (expanded && _doc != null && Exporter.NoteItems(_doc).Count == 0)
            SetStatus("Expanded view is on — add tags or comments to your areas and they will appear around the image.");
        else if (expanded)
            SetStatus("Expanded view: Copy (Ctrl+Shift+C) and Export now include the tags and comments shown.");
        else
            SetStatus("Image only: Copy and Export include the marked areas without the comments.");
    }

    private void ApplyExpandState()
    {
        Surface.NotesLayout = Settings.EffectiveNotesLayout;
        ExpandToggle.IsChecked = Settings.ExpandWithNotes;
    }

    private void ExportImage()
    {
        if (_doc == null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export annotated image",
            Filter = "PNG image|*.png|JPEG image|*.jpg|Bitmap|*.bmp",
            FileName = SafeFileName(_doc.Title) + ".png",
            InitialDirectory = Settings.LastExportFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var timing = Log.Time("export", $"Export {Path.GetExtension(dialog.FileName)} ({Settings.EffectiveNotesLayout})");
            Exporter.SaveImage(Exporter.Render(_doc, CurrentExportOptions), dialog.FileName);
            Settings.LastExportFolder = Path.GetDirectoryName(dialog.FileName);
            _app.SaveSettingsSoon();
            SetStatus($"Exported {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            ShowError("Export failed.", ex);
        }
    }

    private void CopyImage()
    {
        if (_doc == null) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bitmap = Exporter.Render(_doc, CurrentExportOptions);
        Log.Info("export", $"Copy image ({Settings.EffectiveNotesLayout}) rendered {bitmap.PixelWidth}×{bitmap.PixelHeight} in {watch.ElapsedMilliseconds} ms.");
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetImage(bitmap);
                SetStatus(Settings.ExpandWithNotes
                    ? $"Copied {bitmap.PixelWidth} × {bitmap.PixelHeight} with the tags and comments — paste it anywhere."
                    : $"Copied {bitmap.PixelWidth} × {bitmap.PixelHeight} (image only — turn on Expand to include the comments).");
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60); // clipboard briefly locked by another app
            }
        }
        SetStatus("The clipboard is busy — try again.");
    }

    private void CopyNotes()
    {
        if (_doc == null) return;
        try
        {
            Clipboard.SetText(Exporter.NotesAsMarkdown(_doc));
            SetStatus("Notes copied as text.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SetStatus("The clipboard is busy — try again.");
        }
    }

    private async void ExportSnapnote()
    {
        if (_doc == null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export snapshot with its annotations",
            Filter = "Snapshot notes|*" + ProjectFile.Extension,
            FileName = SafeFileName(_doc.Title) + ProjectFile.Extension,
            InitialDirectory = Settings.LastExportFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        var doc = _doc;
        try
        {
            await Task.Run(() => ProjectFile.Write(doc, dialog.FileName));
            Settings.LastExportFolder = Path.GetDirectoryName(dialog.FileName);
            SetStatus($"Exported {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            ShowError("Export failed.", ex);
        }
    }

    private void PasteImage()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                _ = ImportFilesAsync(Clipboard.GetFileDropList().Cast<string>().ToList());
                return;
            }
            if (!Clipboard.ContainsImage()) return;
            var image = Clipboard.GetImage();
            if (image == null) return;
            AddCapture(new CaptureResult(AnnotationDocument.NormalizeImage(image), 1, "Pasted image"));
        }
        catch (Exception ex)
        {
            ShowError("Could not paste the image.", ex);
        }
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await ImportFilesAsync(files);
    }

    // =================================================================== editor commands

    private void Undo_Click(object sender, RoutedEventArgs e) => _undo?.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => _undo?.Redo();
    private void Delete_Click(object sender, RoutedEventArgs e) => Surface.DeleteSelection();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Surface.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Surface.ZoomOut();
    private void ZoomActual_Click(object sender, RoutedEventArgs e) => Surface.ZoomActual();
    private void Fit_Click(object sender, RoutedEventArgs e) => Surface.ZoomToFit();

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<EditorTool>(tag, out var tool)) return;
        Surface.Tool = tool;
        Settings.LastTool = tool;
        _app.SaveSettingsSoon();
        Surface.Focus();
    }

    private void SyncToolButtons()
    {
        _syncing = true;
        var button = Surface.Tool switch
        {
            EditorTool.Select => ToolSelect,
            EditorTool.Square => ToolSquare,
            EditorTool.Ellipse => ToolEllipse,
            EditorTool.Circle => ToolCircle,
            EditorTool.Spline => ToolSpline,
            _ => ToolRectangle,
        };
        button.IsChecked = true;
        _syncing = false;
    }

    private void SetTool(EditorTool tool)
    {
        Surface.Tool = tool;
        Settings.LastTool = tool;
    }

    private void UpdateCommandStates()
    {
        UndoButton.IsEnabled = _undo?.CanUndo == true;
        RedoButton.IsEnabled = _undo?.CanRedo == true;
        DeleteButton.IsEnabled = Surface.Selection.Count > 0;
        ExportButton.IsEnabled = true;
        bool hasDoc = _doc != null;
        CopyButton.IsEnabled = hasDoc;
        ZoomInButton.IsEnabled = ZoomOutButton.IsEnabled = FitButton.IsEnabled = ZoomLevelButton.IsEnabled = hasDoc;
    }

    private void UpdateZoomText() => ZoomText.Text = _doc == null ? "—" : $"{Surface.Zoom * 100:0}%";

    private void UpdateWindowTitle()
    {
        Title = _doc == null ? "Snapshot Notetaker" : $"{_doc.Title} — Snapshot Notetaker";
        ChromeBehavior.SetCaption(this, _doc?.Title ?? "");
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // =================================================================== selection & sidebar

    private void Surface_SelectionChanged(object? sender, EventArgs e)
    {
        SyncProperties();
        UpdateCommandStates();
        var sel = Surface.Selection;
        SelectionText.Text = sel.Count switch
        {
            0 => "",
            1 when !sel[0].ShapeBounds.IsEmpty => $"{sel[0].ShapeBounds.Width:0} × {sel[0].ShapeBounds.Height:0}",
            _ => $"{sel.Count} selected",
        };

        if (_suppressNotesSync) return;
        _suppressNotesSync = true;
        try
        {
            NotesList.SelectedItems.Clear();
            foreach (var a in sel) NotesList.SelectedItems.Add(a);
            if (sel.Count > 0) NotesList.ScrollIntoView(sel[^1]);
        }
        finally
        {
            _suppressNotesSync = false;
        }
    }

    private void NotesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNotesSync) return;
        _suppressNotesSync = true;
        try
        {
            var items = NotesList.SelectedItems.Cast<Annotation>().ToList();
            Surface.SetSelection(items);
            if (items.Count == 1) Surface.BringIntoView(items[0]);
        }
        finally
        {
            _suppressNotesSync = false;
        }
    }

    private void NoteItem_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListBoxItem { IsSelected: false } item)
        {
            NotesList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void UpdateNotesHeader()
    {
        int count = _doc?.Annotations.Count ?? 0;
        NotesCountText.Text = count == 0 ? "" : $"{count} area{(count == 1 ? "" : "s")}";
        NotesEmptyText.Visibility = _doc != null && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FocusComment(Annotation a)
    {
        if (!Settings.ShowNotes) SetNotesVisible(true);
        NotesList.ScrollIntoView(a);
        NotesList.UpdateLayout();
        if (NotesList.ItemContainerGenerator.ContainerFromItem(a) is ListBoxItem container &&
            FindDescendant<TextBox>(container, "CommentBox") is { } box)
        {
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }
    }

    private void NoteUp_Click(object sender, RoutedEventArgs e) => MoveNote(sender, -1);
    private void NoteDown_Click(object sender, RoutedEventArgs e) => MoveNote(sender, +1);

    private void MoveNote(object sender, int delta)
    {
        if (_doc == null || _undo == null || (sender as FrameworkElement)?.DataContext is not Annotation a) return;
        int index = _doc.Annotations.IndexOf(a);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _doc.Annotations.Count) return;
        _undo.Record(() => _doc.Annotations.Move(index, target));
    }

    private void NoteDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _undo == null || (sender as FrameworkElement)?.DataContext is not Annotation a) return;
        _undo.Record(() => _doc.Annotations.Remove(a));
    }

    // Text edits (tags, comments, numbering) become one undo step per focus session.
    private void NoteText_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => _textEditBefore = _doc?.CaptureState();

    private void NoteText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_doc != null && _undo != null && _textEditBefore != null && !_textEditBefore.SameContent(_doc.CaptureState()))
            _undo.Push(_textEditBefore);
        _textEditBefore = null;
    }

    // =================================================================== panels

    private void LibraryToggle_Click(object sender, RoutedEventArgs e) => SetLibraryVisible(!Settings.ShowLibrary);
    private void NotesToggle_Click(object sender, RoutedEventArgs e) => SetNotesVisible(!Settings.ShowNotes);

    private void SetLibraryVisible(bool visible)
    {
        if (!visible && LibraryColumn.ActualWidth > 0) Settings.LibraryWidth = LibraryColumn.ActualWidth;
        Settings.ShowLibrary = visible;
        LibraryPanel.Visibility = LibrarySplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        LibraryColumn.MinWidth = visible ? 160 : 0;
        LibraryColumn.Width = visible ? new GridLength(Math.Clamp(Settings.LibraryWidth, 160, 520)) : new GridLength(0);
        LibraryToggle.IsChecked = visible;
        _app.SaveSettingsSoon();
    }

    private void SetNotesVisible(bool visible)
    {
        if (!visible && NotesColumn.ActualWidth > 0) Settings.NotesWidth = NotesColumn.ActualWidth;
        Settings.ShowNotes = visible;
        NotesPanel.Visibility = NotesSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        NotesColumn.MinWidth = visible ? 220 : 0;
        NotesColumn.Width = visible ? new GridLength(Math.Clamp(Settings.NotesWidth, 220, 640)) : new GridLength(0);
        NotesToggle.IsChecked = visible;
        _app.SaveSettingsSoon();
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (Settings.ShowLibrary) Settings.LibraryWidth = LibraryColumn.ActualWidth;
        if (Settings.ShowNotes) Settings.NotesWidth = NotesColumn.ActualWidth;
        _app.SaveSettingsSoon();
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var name in ThemeManager.Names)
        {
            string theme = name;
            var item = MenuItem(name == ThemeManager.SystemThemeName ? "Follow Windows" : name, () =>
            {
                Settings.Theme = theme;
                ThemeManager.Apply(theme);
                _app.SaveSettingsSoon();
            });
            item.IsChecked = string.Equals(Settings.Theme, name, StringComparison.OrdinalIgnoreCase);
            menu.Items.Add(item);
        }
        OpenMenu(menu, (FrameworkElement)sender);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _app.OpenSettings();

    // =================================================================== property bar

    private void InitPropertyBar()
    {
        _syncing = true;
        Fill(StrokeWidthCombo, StrokeWidths.Select(w => ($"{w:0} px", (object)w)));
        Fill(DashCombo, new (string, object)[] { ("Solid", StrokeDash.Solid), ("Dashed", StrokeDash.Dashed), ("Dotted", StrokeDash.Dotted) });
        Fill(FillCombo, new (string, object)[] { ("No fill", 0.0), ("Tint 15%", 0.15), ("Tint 30%", 0.3), ("Tint 50%", 0.5) });
        Fill(LabelModeCombo, new (string, object)[] { ("No label", LabelMode.None), ("Number", LabelMode.Number), ("Tag", LabelMode.Tag), ("Number + tag", LabelMode.NumberAndTag) });
        Fill(LabelShapeCombo, new (string, object)[] { ("Box", LabelShape.Box), ("Circle", LabelShape.Circle), ("Corner tab", LabelShape.CornerTab), ("Callout", LabelShape.Callout), ("Halo text", LabelShape.Halo) });
        Fill(LabelAnchorCombo, new (string, object)[]
        {
            ("Top left", LabelAnchor.TopLeft), ("Top", LabelAnchor.Top), ("Top right", LabelAnchor.TopRight), ("Right", LabelAnchor.Right),
            ("Bottom right", LabelAnchor.BottomRight), ("Bottom", LabelAnchor.Bottom), ("Bottom left", LabelAnchor.BottomLeft),
            ("Left", LabelAnchor.Left), ("Center", LabelAnchor.Center),
        });
        Fill(LabelPlacementCombo, new (string, object)[] { ("Outside", LabelPlacement.Outside), ("On edge", LabelPlacement.OnEdge), ("Inside", LabelPlacement.Inside) });
        Fill(LabelSchemeCombo, new (string, object)[]
        {
            ("Match shape", LabelColorScheme.MatchShape), ("Adaptive", LabelColorScheme.Adaptive),
            ("Light", LabelColorScheme.Light), ("Dark", LabelColorScheme.Dark), ("Custom", LabelColorScheme.Custom),
        });
        Fill(LabelSizeCombo, FontSizes.Select(s => ($"{s:0}", (object)s)));
        Fill(NumberFormatCombo, new (string, object)[]
        {
            ("1, 2, 3", NumberFormat.Decimal), ("A, B, C", NumberFormat.UpperAlpha), ("a, b, c", NumberFormat.LowerAlpha),
            ("I, II, III", NumberFormat.UpperRoman), ("i, ii, iii", NumberFormat.LowerRoman),
        });
        foreach (var combo in new[] { StrokeWidthCombo, DashCombo, FillCombo, LabelModeCombo, LabelShapeCombo, LabelAnchorCombo, LabelPlacementCombo, LabelSchemeCombo, LabelSizeCombo, NumberFormatCombo })
        {
            combo.DropDownClosed += (_, _) => Surface.Focus();
        }
        _syncing = false;
    }

    /// <summary>Reflects the first selected annotation, or the defaults for new shapes when nothing is selected.</summary>
    private void SyncProperties()
    {
        _syncing = true;
        try
        {
            var a = Surface.Selection.FirstOrDefault();
            var d = Settings.Defaults;
            double s = DocScale;
            var label = a?.Label ?? d.Label;

            StrokeColorSwatch.Background = ColorUtil.Brush(a?.StrokeColor ?? d.StrokeColor);
            SelectNearest(StrokeWidthCombo, a != null ? a.StrokeWidth / s : d.StrokeWidth);
            Select(DashCombo, a?.Dash ?? d.Dash);
            SelectNearest(FillCombo, a?.FillOpacity ?? d.FillOpacity);
            OutlineCheck.IsChecked = a?.StrokeOutline ?? d.StrokeOutline;
            int count = Surface.Selection.Count;
            PropertiesTargetText.Text = count == 0 ? "New areas" : count == 1 ? "Selected area" : $"{count} selected areas";
            PropertiesTarget.ToolTip = count == 0
                ? "Nothing is selected: these settings are used for the next areas you draw."
                : "Changes apply to the selection (and become the style for new areas).";
            ClosedCheck.Visibility = a?.IsSpline == true ? Visibility.Visible : Visibility.Collapsed;
            ClosedCheck.IsChecked = a?.IsSpline == true && a.IsClosed;

            Select(LabelModeCombo, a?.LabelMode ?? d.LabelMode);
            Select(LabelShapeCombo, label.Shape);
            Select(LabelAnchorCombo, label.Anchor);
            Select(LabelPlacementCombo, label.Placement);
            Select(LabelSchemeCombo, label.Scheme);
            LabelColorButton.Visibility = label.Scheme == LabelColorScheme.Custom ? Visibility.Visible : Visibility.Collapsed;
            LabelColorSwatch.Background = ColorUtil.Brush(label.CustomFill);
            SelectNearest(LabelSizeCombo, a != null ? label.FontSize / s : label.FontSize);
            LabelBorderCheck.IsChecked = label.Outline;
            LabelShadowCheck.IsChecked = label.Shadow;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncNumberingControls()
    {
        _syncing = true;
        var n = _doc?.Numbering ?? Settings.Numbering;
        Select(NumberFormatCombo, n.Format);
        if (!PrefixBox.IsKeyboardFocused) PrefixBox.Text = n.Prefix;
        if (!StartBox.IsKeyboardFocused) StartBox.Text = n.Start.ToString();
        _syncing = false;
    }

    /// <summary>Applies a style change to the selection (one undo step) and remembers it for new shapes.</summary>
    private void Apply(Action<ShapeDefaults> setDefault, Action<Annotation> setAnnotation)
    {
        if (_syncing) return;
        setDefault(Settings.Defaults);
        _app.SaveSettingsSoon();
        if (_undo != null && Surface.Selection.Count > 0)
        {
            var selection = Surface.Selection.ToList();
            _undo.Record(() => { foreach (var a in selection) setAnnotation(a); });
        }
        SyncProperties();
    }

    private void ApplyLabel(Func<LabelStyle, LabelStyle> change)
        => Apply(d => d.Label = change(d.Label), a => a.Label = change(a.Label));

    private void StrokeColor_Click(object sender, RoutedEventArgs e)
    {
        var current = Surface.Selection.FirstOrDefault()?.StrokeColor ?? Settings.Defaults.StrokeColor;
        ColorPicker.Show(StrokeColorButton, current, c => Apply(d => d.StrokeColor = c, a => a.StrokeColor = c));
    }

    private void LabelColor_Click(object sender, RoutedEventArgs e)
    {
        var current = (Surface.Selection.FirstOrDefault()?.Label ?? Settings.Defaults.Label).CustomFill;
        ColorPicker.Show(LabelColorButton, current, c => ApplyLabel(l => l with { CustomFill = c, CustomText = ColorUtil.BestTextOn(c) }));
    }

    private void StrokeWidth_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Selected<double>(StrokeWidthCombo) is double w) Apply(d => d.StrokeWidth = w, a => a.StrokeWidth = w * DocScale);
    }

    private void Dash_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Selected<StrokeDash>(DashCombo) is StrokeDash v) Apply(d => d.Dash = v, a => a.Dash = v);
    }

    private void Fill_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Selected<double>(FillCombo) is double v) Apply(d => d.FillOpacity = v, a => a.FillOpacity = v);
    }

    private void Outline_Click(object sender, RoutedEventArgs e)
    {
        bool v = OutlineCheck.IsChecked == true;
        Apply(d => d.StrokeOutline = v, a => a.StrokeOutline = v);
    }

    private void Closed_Click(object sender, RoutedEventArgs e)
    {
        bool v = ClosedCheck.IsChecked == true;
        Apply(_ => { }, a => { if (a.IsSpline && a.Points.Length >= 3) a.IsClosed = v; });
    }

    private void LabelMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Selected<LabelMode>(LabelModeCombo) is LabelMode v) Apply(d => d.LabelMode = v, a => a.LabelMode = v);
    }

    private void LabelShape_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Selected<LabelShape>(LabelShapeCombo) is LabelShape v)
            Apply(d => d.Label = d.Label with { Shape = v }, a => { a.Label = a.Label with { Shape = v }; a.LabelOffset = default; });
    }

    private void LabelAnchor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Selected<LabelAnchor>(LabelAnchorCombo) is LabelAnchor v)
            Apply(d => d.Label = d.Label with { Anchor = v }, a => { a.Label = a.Label with { Anchor = v }; a.LabelOffset = default; });
    }

    private void LabelPlacement_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Selected<LabelPlacement>(LabelPlacementCombo) is LabelPlacement v)
            Apply(d => d.Label = d.Label with { Placement = v }, a => { a.Label = a.Label with { Placement = v }; a.LabelOffset = default; });
    }

    private void LabelScheme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Selected<LabelColorScheme>(LabelSchemeCombo) is LabelColorScheme v) ApplyLabel(l => l with { Scheme = v });
    }

    private void LabelSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Selected<double>(LabelSizeCombo) is double v)
            Apply(d => d.Label = d.Label with { FontSize = v }, a => a.Label = a.Label with { FontSize = v * DocScale });
    }

    private void LabelBorder_Click(object sender, RoutedEventArgs e)
    {
        bool v = LabelBorderCheck.IsChecked == true;
        ApplyLabel(l => l with { Outline = v });
    }

    private void LabelShadow_Click(object sender, RoutedEventArgs e)
    {
        bool v = LabelShadowCheck.IsChecked == true;
        ApplyLabel(l => l with { Shadow = v });
    }

    private void ApplyLabelToAll_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _undo == null) return;
        var source = Surface.Selection.FirstOrDefault();
        var mode = source?.LabelMode ?? Settings.Defaults.LabelMode;
        var style = source?.Label ?? Settings.Defaults.Label with { FontSize = Settings.Defaults.Label.FontSize * DocScale };
        _undo.Record(() =>
        {
            foreach (var a in _doc.Annotations)
            {
                a.LabelMode = mode;
                a.Label = style;
                a.LabelOffset = default;
            }
        });
        SetStatus("Label style applied to every area.");
    }

    private void NumberFormat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Selected<NumberFormat>(NumberFormatCombo) is not NumberFormat v) return;
        Settings.Numbering = Settings.Numbering with { Format = v };
        _app.SaveSettingsSoon();
        if (_doc != null && _undo != null) _undo.Record(() => _doc.Numbering = _doc.Numbering with { Format = v });
    }

    private void Numbering_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        int start = int.TryParse(StartBox.Text.Trim(), out int s) ? Math.Clamp(s, 0, 99999) : 1;
        Settings.Numbering = Settings.Numbering with { Prefix = PrefixBox.Text, Start = start };
        _app.SaveSettingsSoon();
        if (_doc != null) _doc.Numbering = _doc.Numbering with { Prefix = PrefixBox.Text, Start = start };
    }

    // =================================================================== keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool typing = Keyboard.FocusedElement is TextBoxBase;
        bool handled = true;

        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.O: Import(); break;
                case Key.E: ExportImage(); break;
                case Key.S: SaveNow(); SetStatus("All changes saved."); break;
                case Key.Z when !typing: _undo?.Undo(); break;
                case Key.Y when !typing: _undo?.Redo(); break;
                case Key.D when !typing: Surface.DuplicateSelection(); break;
                case Key.A when !typing: Surface.SelectAll(); break;
                case Key.V when !typing: PasteImage(); break;
                case Key.L: SetLibraryVisible(!Settings.ShowLibrary); break;
                case Key.B: SetNotesVisible(!Settings.ShowNotes); break;
                case Key.F when Settings.ShowLibrary: SearchBox.Focus(); SearchBox.SelectAll(); break;
                case Key.D0 or Key.NumPad0: Surface.ZoomToFit(); break;
                case Key.D1 or Key.NumPad1: Surface.ZoomActual(); break;
                case Key.OemPlus or Key.Add: Surface.ZoomIn(); break;
                case Key.OemMinus or Key.Subtract: Surface.ZoomOut(); break;
                case Key.OemComma: _app.OpenSettings(); break;
                case Key.N: _app.RunAction(HotkeyAction.CaptureRegion); break;
                default: handled = false; break;
            }
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.Z when !typing: _undo?.Redo(); break;
                case Key.C: CopyImage(); break;
                case Key.E: SetExpanded(!Settings.ExpandWithNotes); break;
                default: handled = false; break;
            }
        }
        else if (typing)
        {
            if (key == Key.Escape) Surface.Focus();
            else handled = false;
        }
        else if (mods is ModifierKeys.None or ModifierKeys.Shift)
        {
            double step = mods == ModifierKeys.Shift ? 10 : 1;
            bool onSurface = Keyboard.FocusedElement == Surface;
            switch (key)
            {
                case Key.V when mods == ModifierKeys.None: SetTool(EditorTool.Select); break;
                case Key.R when mods == ModifierKeys.None: SetTool(EditorTool.Rectangle); break;
                case Key.Q when mods == ModifierKeys.None: SetTool(EditorTool.Square); break;
                case Key.E when mods == ModifierKeys.None: SetTool(EditorTool.Ellipse); break;
                case Key.C when mods == ModifierKeys.None: SetTool(EditorTool.Circle); break;
                case Key.P or Key.S when mods == ModifierKeys.None: SetTool(EditorTool.Spline); break;
                case Key.Delete or Key.Back: Surface.DeleteSelection(); break;
                case Key.Escape: Surface.Escape(); break;
                case Key.Enter when Surface.HasPendingSpline: Surface.FinishPendingSpline(); break;
                case Key.Left when onSurface: Surface.Nudge(new Vector(-step, 0)); break;
                case Key.Right when onSurface: Surface.Nudge(new Vector(step, 0)); break;
                case Key.Up when onSurface: Surface.Nudge(new Vector(0, -step)); break;
                case Key.Down when onSurface: Surface.Nudge(new Vector(0, step)); break;
                case Key.Space: if (!e.IsRepeat) Surface.SetSpacePan(true); break;
                case Key.F2: BeginRename(); break;
                default: handled = false; break;
            }
        }
        else handled = false;

        if (handled) e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) Surface.SetSpacePan(false);
    }

    // =================================================================== window lifetime

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveNow();
        if (Settings.ShowLibrary && LibraryColumn.ActualWidth > 0) Settings.LibraryWidth = LibraryColumn.ActualWidth;
        if (Settings.ShowNotes && NotesColumn.ActualWidth > 0) Settings.NotesWidth = NotesColumn.ActualWidth;
        _app.SaveSettingsSoon();

        if (!_app.IsExiting && Settings.KeepRunningInTray)
        {
            e.Cancel = true;
            Hide();
            _app.NotifyRunningInTray();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_app.IsExiting) _app.ExitApp();
    }

    // =================================================================== helpers

    private static MenuItem MenuItem(string header, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = new TextBlock { Text = header }, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => action();
        return item;
    }

    private static void OpenMenu(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = 2;
        menu.IsOpen = true;
    }

    private static void Fill(ComboBox combo, IEnumerable<(string Text, object Value)> items)
    {
        combo.Items.Clear();
        foreach (var (text, value) in items) combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
    }

    private static void Select(ComboBox combo, object value)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (Equals(item.Tag, value))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = -1;
    }

    private static void SelectNearest(ComboBox combo, double value)
    {
        ComboBoxItem? best = null;
        double bestDistance = double.MaxValue;
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag is not double v) continue;
            double d = Math.Abs(v - value);
            if (d < bestDistance) { bestDistance = d; best = item; }
        }
        combo.SelectedItem = best;
    }

    private static T? Selected<T>(ComboBox combo) where T : struct
        => combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : null;

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            var found = FindDescendant<T>(child, name);
            if (found != null) return found;
        }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "Snapshot" : cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    /// <param name="logMessage">What goes in the log when <paramref name="message"/> contains user content (e.g. a file name).</param>
    private void ShowError(string message, Exception ex, string? logMessage = null)
    {
        Log.Error("ui", logMessage ?? message, ex);
        ErrorReporting.Capture(ex);
        SetStatus(message);
        ProblemWindow.ShowError(this, message, ex);
    }
}
