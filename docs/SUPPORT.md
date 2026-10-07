# Supporting users

This guide is for maintainers and anyone distributing builds to other people. It covers what the app records, how users get diagnostics to you, and how to turn on remote crash reporting.

## What the app records

| | Where (on the user's PC) | Notes |
|---|---|---|
| Log | `%LOCALAPPDATA%\SnapshotNotetaker\logs\app.log` (+ `app.1.log`…`app.4.log`, 2 MB each) | Info level by default; *Settings → Diagnostics → Detailed logging* adds debug entries. Each run starts with the version, session id, start-up phase timings (`Ready in … ms (runtime …, window …, show …)`), shortcut registration results and a system summary (Windows/.NET version, displays and DPI, graphics tier, library size, options). |
| Crash reports | `…\crashes\crash-<time>[-fatal].txt` | Written for every unhandled error. UI-thread errors are recovered from, and the user sees a dialog. Background-thread errors end the process; those reports end in `-fatal`, and on the next start the app offers a support bundle. |
| Remote reports | Sentry (optional) | See below. |

Messages are written so they can be shared: no snapshot titles, comments, window titles or file names, and personal paths and names are masked by `Support/Privacy.cs`. **Keep it that way when adding log lines.** Log ids, sizes, counts, durations and error types instead.

## Getting diagnostics from a user

Ask them to click **? → Help & support → Create support bundle**. The zip contains:

```
README.txt          what's inside
system-info.txt     version, session, Windows/.NET, displays, shortcuts (and which failed), options
description.txt     what the user typed (optional)
logs/               app.log and rolled files
crashes/            the 10 newest crash/error reports
settings.json       personal paths masked
snapshot/           only if the user ticked "Include the current snapshot"
```

Useful things to look for:

- `WRN [hotkeys]` lines show shortcuts another app already owns. Snagit and ShareX commonly take PrintScreen.
- `Ready in … ms (…)` breaks start-up time into phases.
- `[capture] CaptureRegion took … ms (1920×1080 at 150%)` shows capture timings, sizes and the DPI of the source display.
- `ERR`/`FTL` entries include the full exception.

The session id shown in *Help & support → About* matches the `starting (session …)` line in the log.

## Remote crash reporting (optional)

Builds don't send anything by default. To receive crash reports from the people using your builds:

1. Create a (free) project at [sentry.io](https://sentry.io) of type *.NET* and copy its DSN.
2. Build with the DSN:
   ```powershell
   ./scripts/publish.ps1 -SentryDsn "https://<key>@<org>.ingest.sentry.io/<project>" -SupportUrl "https://github.com/<you>/<repo>/issues"
   ```
   In GitHub Actions, store the DSN as the repository secret `SENTRY_DSN`. Optionally set the repository variables `SUPPORT_EMAIL` and `SUPPORT_URL`; the release workflow passes them through, and `SUPPORT_URL` defaults to the repo's Issues page.
3. When a user first starts such a build, they're asked once whether to send reports. They can change their answer in Settings or in Help & support.

What Sentry receives:

- handled and unhandled exceptions with stack traces (line numbers included, because the release build embeds symbols)
- recent log lines as breadcrumbs
- release `snapshot-notetaker@<version>` and the environment
- an anonymous install id
- release-health sessions (crash-free rate)

`ServerName` is cleared. The app doesn't send default personal information, and it scrubs exception messages before they are sent. As a belt-and-braces measure, also turn on *Prevent Storing of IP Addresses* in the Sentry project's security settings. Sentry is initialised only when a DSN is present *and* the user has opted in; `Support/ErrorReporting.cs` is the only file that talks to Sentry, so swapping in another service means replacing that one file.

For local testing without rebuilding, set the environment variable `SNAPSHOT_NOTETAKER_SENTRY_DSN`.

## Support contact shown in the app

`-SupportEmail` and/or `-SupportUrl` (MSBuild properties `SupportEmail`/`SupportUrl`) add a *Contact support* button to Help & support. Without them, the app tells users to send the bundle to "the person or site you got Snapshot Notetaker from".
