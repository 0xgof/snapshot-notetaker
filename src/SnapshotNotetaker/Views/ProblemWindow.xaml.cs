using System.IO;
using System.Windows;
using SnapshotNotetaker.Support;
using SnapshotNotetaker.Themes;

namespace SnapshotNotetaker.Views;

/// <summary>Friendly error dialog with the tools a user needs to get help: support bundle, copy details, send report.</summary>
public partial class ProblemWindow : Window
{
    private static bool _isOpen;
    private readonly Exception? _exception;
    private readonly string _details;

    private ProblemWindow(string headline, string explanation, string details, Exception? exception)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        _exception = exception;
        _details = details;
        HeadlineText.Text = headline;
        ExplanationText.Text = explanation;
        DetailsBox.Text = details;

        bool canSend = exception != null && ErrorReporting.IsAvailable;
        SendButton.Visibility = canSend && !ErrorReporting.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (canSend && ErrorReporting.IsEnabled) StatusText.Text = "This problem was reported automatically.";
    }

    /// <summary>An error the app recovered from.</summary>
    public static void ShowError(Window? owner, string headline, Exception exception)
        => Show(owner, headline,
            "You can keep working; your snapshots are saved automatically. If this keeps happening, create a support bundle and send it to support.",
            Privacy.Scrub(exception.ToString()), exception);

    /// <summary>A non-modal instance for DevTools previews.</summary>
    internal static ProblemWindow CreateForPreview(Exception exception)
        => new("Something went wrong", "You can keep working; your snapshots are saved automatically. If this keeps happening, create a support bundle and send it to support.",
               Privacy.Scrub(exception.ToString()), exception);

    /// <summary>The app was closed by a crash during the previous run.</summary>
    public static void ShowPreviousCrash(Window? owner, FileInfo report)
    {
        string details;
        try { details = File.ReadAllText(report.FullName); }
        catch { details = report.FullName; }
        Show(owner, "Snapshot Notetaker closed unexpectedly last time",
            "Your snapshots were saved up to that moment. Sending a support bundle to support helps get this fixed.",
            details, null);
    }

    private static void Show(Window? owner, string headline, string explanation, string details, Exception? exception)
    {
        if (_isOpen) return; // never stack dialogs (e.g. an error that repeats on every render)
        _isOpen = true;
        try
        {
            var window = new ProblemWindow(headline, explanation, details, exception);
            if (owner is { IsVisible: true }) window.Owner = owner;
            else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error("ui", "Could not show the problem dialog.", ex);
        }
        finally
        {
            _isOpen = false;
        }
    }

    private void DetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = DetailsBox.Visibility != Visibility.Visible;
        DetailsBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailsToggle.Content = show ? "Hide technical details" : "Show technical details";
    }

    private async void Bundle_Click(object sender, RoutedEventArgs e)
    {
        if (await SupportActions.CreateBundleAsync(this) is { } path)
            StatusText.Text = $"Support bundle saved as {Path.GetFileName(path)}. Attach it to an email to support.";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        string text = _details + Environment.NewLine + Environment.NewLine + SystemInfo.Describe();
        StatusText.Text = SupportActions.TrySetClipboard(text) ? "Details copied to the clipboard." : "The clipboard is busy; try again.";
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_exception == null) return;
        SendButton.IsEnabled = false;
        bool sent = ErrorReporting.SendOnce(_exception);
        StatusText.Text = sent ? "Report sent. Thank you!" : "The report could not be sent; create a support bundle instead.";
        if (!sent) SendButton.IsEnabled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
