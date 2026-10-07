# Privacy

Snapshot Notetaker is a local app. This page lists everything it stores and everything that can ever leave your PC.

## What is stored on your PC

All of it is under `%LOCALAPPDATA%\SnapshotNotetaker` unless you moved the library in Settings.

| What | Where | Contents |
|---|---|---|
| Snapshots | `Snapshots\<date-id>\` | `image.png` (the original capture), `document.json` (title, areas, tags, comments), `thumb.png` (preview) |
| Settings | `settings.json` | Your preferences, shortcuts, an anonymous install id |
| Logs | `logs\app.log` (up to 5 files of 2 MB) | Technical events: start-up, capture and save timings, errors |
| Crash reports | `crashes\` (last 25) | The error, system details, recent log lines |

**Logs and crash reports never contain** your screenshots, comments, snapshot titles, the titles of other apps' windows or file names. Your Windows user name and computer name are masked (for example `%USERPROFILE%\…` or `<computer>`).

Deleting `%LOCALAPPDATA%\SnapshotNotetaker` removes everything.

## What can leave your PC

**Nothing, unless you send it.** The app makes no network connections. The only exceptions are these:

1. **Crash reports.** These are only possible in builds where the publisher configured a crash-reporting service ([Sentry](https://sentry.io)), and only after you choose *Send reports* (you can change this any time in Settings or Help & support). A report contains:
   - the error and stack trace
   - the app version, Windows version, .NET version and display setup (sizes and scaling)
   - recent log lines, which are already scrubbed as described above
   - a random install id, so reports from the same install can be grouped

   It never contains screenshots, notes, titles, your user name or your computer name. The app also tells Sentry not to record your IP address (it sends no default personal information). Reports wait in `reports-outbox\` until they can be sent.
2. **Support bundles** are zips *you* create from Help & support and send yourself. They contain the logs, crash reports, system details and settings (with personal paths masked). They only include a snapshot if you tick *Include the current snapshot*.

Copying an image to the clipboard or exporting a file puts that content where you choose; the app doesn't send it anywhere.
