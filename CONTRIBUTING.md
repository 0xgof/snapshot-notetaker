# Contributing

Thanks for helping! Bug reports, feature ideas and pull requests are all welcome.

## Reporting a bug

[Open an issue](../../issues/new/choose) with the bug template. Please attach a **support bundle** (in the app: **? → Help & support → Create support bundle**). It has the version, logs and system details needed to reproduce the problem, and no screenshots unless you choose to include one.

## Making a change

1. Fork the repo and create a branch from `main`.
2. Set up the tools as described in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) (Windows + .NET 10 SDK).
3. Make your change. Keep it focused; one topic per pull request.
4. Run `dotnet test`. Add tests for logic changes (model, rendering math, persistence, support code).
5. For visual changes, attach before/after pictures. `SnapshotNotetaker.exe --ui-snapshot out.png Light` renders the main window with demo content.
6. Add a line under *Unreleased* in [CHANGELOG.md](CHANGELOG.md).
7. Open the pull request and fill in the template.

## Guidelines

- **Match the surrounding code:** file-scoped namespaces, `_camelCase` private fields, comments that explain *why* rather than restating the code.
- **Performance matters.** The editor re-renders only what changed; keep work off the UI thread when it touches the disk; avoid per-frame allocations in input handlers.
- **Privacy:** log messages and crash reports must not contain user content (snapshot titles, comments, other windows' titles, file names). Log ids, sizes, counts and durations instead.
- **Compatibility:** `document.json`, `settings.json` and `.snapnote` files from earlier versions must keep loading; `CompatibilityTests` guards this.
- **Themes:** use `DynamicResource Brush.*` for every UI colour so all themes work; check at least one light and one dark theme.

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
