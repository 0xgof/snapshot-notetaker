# Security policy

## Supported versions

Security fixes go into the latest release. Please update to it before reporting.

## Reporting a vulnerability

**Please don't open a public issue.** Use GitHub's private reporting instead: go to the **Security** tab of this repository and click **Report a vulnerability**. Include the version, steps to reproduce, and the impact you see.

You'll get an acknowledgement within a few days. Once a fix is released, the advisory will credit you, unless you prefer otherwise.

## Scope notes

- The app makes no network connections, except opt-in crash reports in builds configured with a Sentry DSN (see [docs/PRIVACY.md](docs/PRIVACY.md)).
- It reads screen contents only when you start a capture, and stores snapshots under your user profile (or the folder you choose).
- `.snapnote` files are zip archives containing a PNG and JSON. Reports about crafted files that crash the app or escape their folder are in scope.
