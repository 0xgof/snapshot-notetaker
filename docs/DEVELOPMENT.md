# Development

## Prerequisites

- Windows 10 1809+ or Windows 11
- [.NET 10 SDK](https://dot.net) (pinned by `global.json`, which rolls forward to newer feature bands)
- Any editor: Visual Studio 2026, Rider or VS Code with C# Dev Kit

```powershell
dotnet build                                   # Debug build of the app and tests
dotnet test                                    # unit tests
dotnet run --project src/SnapshotNotetaker     # run the app
./scripts/publish.ps1 -Runtime win-x64,win-arm64   # release zips in ./artifacts
```

The app is single-instance. A second launch just brings the running one to the front, and closing the window keeps it in the tray, so use **tray → Exit** before rebuilding (the running exe is locked).

## Project layout

```
src/SnapshotNotetaker/
  Capture/     GDI/PrintWindow capture, per-monitor overlay picker, global hotkeys
  Model/       Annotation, AnnotationDocument (live renumbering), undo, label/numbering options
  Rendering/   centripetal Catmull-Rom splines, shapes, labels, colour contrast,
               NotesComposition (the "expanded" handover layout shared by the editor and export)
  Editor/      AnnotationCanvas (retained visuals) and EditorSurface (zoom/pan, tools, handles, hit testing)
  IO/          snapshot library (autosave), .snapnote format, export, source-generated JSON
  Settings/    AppSettings (settings.json)
  Support/     logging, privacy scrubbing, crash handling, support bundles, optional Sentry reporting
  Themes/      ThemeManager (runtime themes) and themed control templates, custom title bar
  Views/       main window, settings, help & support, problem dialog, tray icon, small controls
  DevTools.cs  diagnostic switches (below)
tests/SnapshotNotetaker.Tests/   xUnit tests
scripts/publish.ps1              release build (self-contained single-file exe + zip + checksums)
```

### Design notes

- **Rendering:** each annotation has its own `DrawingVisual` for the shape and one for its label (labels sit on a layer above all shapes). Only changed annotations are re-rendered, coalesced once per frame. Zoom and pan are a single `MatrixTransform`.
- **Coordinates:** the image is 96 DPI, so 1 image pixel = 1 DIP. Annotations are stored in image pixels. The editor's zoom is expressed in *device* pixels per image pixel, so 100 % means 1:1 on any monitor.
- **Capture:** the process is per-monitor-v2 DPI aware (`app.manifest`). The overlay uses one window per monitor, each at that monitor's DPI, sharing a `CaptureSession` in physical pixels. `DevTools --overlay-test` verifies the placement.
- **Library:** one folder per snapshot. `image.png` is written once. Edits only rewrite `document.json` and `thumb.png`, from a sequential background write queue, with autosave about 0.7 s after the last change.
- **Privacy:** log messages must not contain user content (titles, comments, window titles, file names); see [SUPPORT.md](SUPPORT.md).

## Diagnostic switches

None of these touch your settings, library or logs.

```powershell
SnapshotNotetaker.exe --render-demo <dir>                       # demo snapshot → every export layout + .snapnote round trip
SnapshotNotetaker.exe --ui-snapshot <file.png> [theme] [--expand] [--narrow]   # picture of the main window (demo library)
SnapshotNotetaker.exe --dialog-snapshot settings|support|problem <file.png> [theme]
SnapshotNotetaker.exe --capture-test <dir>                      # capture each display, report sizes/brightness
SnapshotNotetaker.exe --overlay-test <dir>                      # check overlays cover each monitor exactly
```

The README screenshots in `docs/images` were made with `--ui-snapshot` and `--render-demo`.

## Releasing

1. Update `CHANGELOG.md`, then commit.
2. Tag and push: `git tag v0.3.0 && git push origin v0.3.0`.
3. The **Release** workflow runs the tests, builds `win-x64` and `win-arm64` single-file zips (versioned from the tag) and `SHA256SUMS.txt`, and creates a **draft** GitHub release with generated notes.
4. Review the draft and publish it.

Optional repository settings for release builds (Settings → Secrets and variables → Actions):

| Name | Kind | Purpose |
|---|---|---|
| `SENTRY_DSN` | secret | Enables the opt-in crash reporting in released builds ([SUPPORT.md](SUPPORT.md)) |
| `SUPPORT_URL` | variable | *Contact support* link in the app (default: this repo's Issues) |
| `SUPPORT_EMAIL` | variable | Support email shown in the app |

### Code signing

Releases are unsigned, so Windows SmartScreen warns on first run. For a smoother experience, sign `SnapshotNotetaker.exe` before zipping. [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/) and [SignPath Foundation](https://signpath.org) (free for open source) both work from GitHub Actions; add a signing step after `dotnet publish` in `scripts/publish.ps1`.
