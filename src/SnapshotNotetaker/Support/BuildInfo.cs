using System.Reflection;

namespace SnapshotNotetaker.Support;

/// <summary>
/// Version and distribution settings baked in at build time, e.g.
/// <c>dotnet publish -p:SentryDsn=https://…ingest.sentry.io/… -p:SupportEmail=help@example.com -p:SupportUrl=https://…</c>.
/// The environment variable SNAPSHOT_NOTETAKER_SENTRY_DSN overrides the DSN (handy for testing).
/// </summary>
public static class BuildInfo
{
    private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;

    public static string Version
        => Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
           ?? Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string? SentryDsn
        => Environment.GetEnvironmentVariable("SNAPSHOT_NOTETAKER_SENTRY_DSN") is { Length: > 0 } fromEnvironment ? fromEnvironment : Metadata("SentryDsn");

    public static string? SupportEmail => Metadata("SupportEmail");
    public static string? SupportUrl => Metadata("SupportUrl");

#if DEBUG
    public static string DeploymentEnvironment => "development";
#else
    public static string DeploymentEnvironment => Metadata("DeploymentEnvironment") ?? "production";
#endif

    private static string? Metadata(string key)
        => Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } value
            ? value
            : null;
}
