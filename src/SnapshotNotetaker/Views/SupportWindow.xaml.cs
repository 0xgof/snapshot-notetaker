using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using SnapshotNotetaker.Support;
using SnapshotNotetaker.Themes;

namespace SnapshotNotetaker.Views;

/// <summary>Help &amp; support: support bundle, diagnostic summary, logs, crash-report consent and version info.</summary>
public partial class SupportWindow : Window
{
    private readonly App _app;

    public SupportWindow(App app)
    {
        _app = app;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        string contact = BuildInfo.SupportEmail ?? BuildInfo.SupportUrl ?? "the person or site you got Snapshot Notetaker from";
        IntroText.Text = "Create a support bundle and send it to " + contact + ". It contains technical logs, crash reports and system details. "
                         + "Your screenshots and notes are not included unless you tick the box below.";
        ContactButton.Visibility = BuildInfo.SupportEmail != null || BuildInfo.SupportUrl != null ? Visibility.Visible : Visibility.Collapsed;
        IncludeSnapshotCheck.IsEnabled = app.CurrentSnapshotFolder != null;

        if (ErrorReporting.IsAvailable)
        {
            CrashReportsCheck.IsChecked = app.Settings.SendCrashReports == true;
            CrashReportsHelp.Text = "When the app hits an error, a report with the technical details (error, app and Windows version, displays) "
                                    + "is sent to the developer. Screenshots, notes, titles, your user name and computer name are never sent.";
        }
        else
        {
            CrashReportsCheck.IsEnabled = false;
            CrashReportsHelp.Text = "This build has no remote crash reporting. Crash reports are kept on this PC and included in support bundles.";
        }

        AddAbout("Version", $"{BuildInfo.Version} ({BuildInfo.DeploymentEnvironment})");
        AddAbout("Session", Log.SessionId);
        AddAbout("Windows", RuntimeInformation.OSDescription);
        AddAbout(".NET", RuntimeInformation.FrameworkDescription);
        AddAbout("Logs", Log.Folder);
        AddAbout("Crash reports", CrashHandler.Folder);
    }

    private void AddAbout(string key, string value)
    {
        int row = AboutGrid.RowDefinitions.Count;
        AboutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var k = new TextBlock { Text = key, Margin = new Thickness(0, 2, 0, 2) };
        k.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Muted");
        var v = new TextBox { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = null, Padding = new Thickness(0), MinHeight = 0, Margin = new Thickness(0, 2, 0, 2) };
        Grid.SetRow(k, row);
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        AboutGrid.Children.Add(k);
        AboutGrid.Children.Add(v);
    }

    private async void Bundle_Click(object sender, RoutedEventArgs e)
    {
        if (await SupportActions.CreateBundleAsync(this, DescriptionBox.Text, IncludeSnapshotCheck.IsChecked == true) is { } path)
            StatusText.Text = $"Saved {Path.GetFileName(path)}. Attach it to your message to support.";
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
        => StatusText.Text = SupportActions.CopySummary() ? "Diagnostic summary copied; paste it into your message." : "The clipboard is busy; try again.";

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => SupportActions.OpenLogFolder();

    private void Contact_Click(object sender, RoutedEventArgs e) => SupportActions.OpenSupportContact();

    private void CrashReports_Click(object sender, RoutedEventArgs e)
    {
        _app.SetCrashReporting(CrashReportsCheck.IsChecked == true);
        StatusText.Text = CrashReportsCheck.IsChecked == true ? "Crash reports will be sent automatically." : "Crash reports stay on this PC.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
