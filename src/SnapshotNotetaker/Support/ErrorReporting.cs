using System.IO;
using Sentry;
using SnapshotNotetaker.Settings;

namespace SnapshotNotetaker.Support;

/// <summary>
/// Optional remote crash reporting through Sentry. Inert unless the build carries a DSN (see <see cref="BuildInfo"/>)
/// and the user has opted in. Events carry stack traces, app/OS/display facts, recent log lines as breadcrumbs and an
/// anonymous install id — never screenshots, notes, titles, the Windows user name or the computer name.
/// </summary>
public static class ErrorReporting
{
    private static IDisposable? _sdk;
    private static string _installId = "";

    public static bool IsAvailable => !string.IsNullOrWhiteSpace(BuildInfo.SentryDsn);
    public static bool IsEnabled => _sdk != null;

    /// <summary>Starts or stops automatic reporting to match the user's choice.</summary>
    public static void Apply(bool consent, string installId)
    {
        _installId = installId;
        if (consent && IsAvailable && _sdk == null) Start();
        else if (!consent && _sdk != null) Stop();
    }

    /// <summary>Reports a handled error (e.g. a UI-thread exception the app recovered from).</summary>
    public static void Capture(Exception ex)
    {
        if (_sdk != null) SentrySdk.CaptureException(ex);
    }

    /// <summary>Sends one report even while automatic reporting is off — the user clicked "Send report".</summary>
    public static bool SendOnce(Exception ex)
    {
        if (!IsAvailable) return false;
        bool temporary = _sdk == null;
        try
        {
            if (temporary) Start(sessionTracking: false);
            var id = SentrySdk.CaptureException(ex);
            SentrySdk.Flush(TimeSpan.FromSeconds(5));
            Log.Info("support", $"Sent error report {id}.");
            return id != SentryId.Empty;
        }
        catch (Exception sendError)
        {
            Log.Warn("support", "Could not send the error report.", sendError);
            return false;
        }
        finally
        {
            if (temporary) Stop();
        }
    }

    public static void Flush()
    {
        if (_sdk != null) SentrySdk.Flush(TimeSpan.FromSeconds(3));
    }

    private static void Start(bool sessionTracking = true)
    {
        _sdk = SentrySdk.Init(o =>
        {
            o.Dsn = BuildInfo.SentryDsn;
            o.Release = $"snapshot-notetaker@{BuildInfo.Version}";
            o.Environment = BuildInfo.DeploymentEnvironment;
            o.IsGlobalModeEnabled = true;          // desktop app: one scope for the whole process
            o.AutoSessionTracking = sessionTracking; // crash-free rate per release
            o.SendDefaultPii = false;
            o.AttachStacktrace = true;
            o.MaxBreadcrumbs = 100;
            o.CacheDirectoryPath = Path.Combine(AppSettings.AppDataFolder, "reports-outbox"); // survives being offline
            o.SetBeforeSend((e, _) => Scrub(e));
        });
        SentrySdk.ConfigureScope(scope => scope.User = new SentryUser { Id = _installId });
        Log.Written += AddBreadcrumb;
        if (sessionTracking) Log.Info("support", "Automatic crash reporting is on.");
    }

    private static void Stop()
    {
        Log.Written -= AddBreadcrumb;
        try { SentrySdk.Flush(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _sdk?.Dispose();
        _sdk = null;
    }

    private static SentryEvent Scrub(SentryEvent e)
    {
        e.ServerName = null; // the computer name
        if (e.SentryExceptions != null)
            foreach (var exception in e.SentryExceptions) exception.Value = Privacy.Scrub(exception.Value);
        if (e.Message?.Formatted is { } formatted) e.Message = new SentryMessage { Formatted = Privacy.Scrub(formatted) };
        return e;
    }

    private static void AddBreadcrumb(LogLevel level, string category, string message)
        => SentrySdk.AddBreadcrumb(message, category, level: level switch
        {
            LogLevel.Warn => BreadcrumbLevel.Warning,
            LogLevel.Error => BreadcrumbLevel.Error,
            LogLevel.Fatal => BreadcrumbLevel.Fatal,
            _ => BreadcrumbLevel.Info,
        });
}
