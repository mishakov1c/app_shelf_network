# NetworkPlugin for AppShelf

Development plan: [ROADMAP.md](ROADMAP.md).

Independent network diagnostics plugin for [AppShelf](https://github.com/mishakov1c/app_shelf), using plugin API v1.

- Two configurable checks: default ICMP + HTTPS with TCP fallback, ICMP, HTTPS, TCP.
- Eight-second background monitoring, three optional bottom-panel indicators.
- Manual DNS/HTTPS diagnostics, Windows route, active adapters and proxy report.
- Separate settings in the host-provided plugin data directory; `network-settings.json` is the only file allowed for AppShelf backups, after explicit user consent. No writes to Windows network settings.
- Happ recovery is not included.

## Build

Double-click `Build-Plugin.cmd` in the repository root to build the `.asplugin` package and open the `dist` folder. It does not install or start the plugin. For unattended builds, run `Build-Plugin.cmd --no-open --no-pause`; the compiler exit status is propagated.

On Windows with .NET Framework 4.8, run in Windows PowerShell 5.1:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build.ps1
```

Output: `dist/NetworkPlugin-1.0.3.asplugin`. The build uses the local API v1 protocol source and does not require AppShelf.exe, a host source checkout or internet access.

### Official icon

`Assets/NetworkPlugin.Source.png` is the approved original image, preserved unchanged.
`Assets/NetworkPlugin.png` removes transparent margins and detached export speckles while preserving the main artwork and its proportions.
`Assets/NetworkPlugin.ico` contains 32-bit frames at 16, 20, 24, 32, 48, 64, 128 and 256 pixels.
Regenerate both derived files with `powershell.exe -NoProfile -ExecutionPolicy Bypass -File Assets/Build-Icon.ps1`.
The ICO is embedded both as the Windows EXE icon and as a managed resource shared by the diagnostics and settings WPF windows; no external image files are required at runtime.

## Tests

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tests/NetworkPlugin.Tests.ps1
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tests/NetworkMonitor.Tests.ps1
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tests/NetworkRuntime.Tests.ps1 -HostSourceDirectory C:\path\to\app_shelf
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tests/NetworkIcon.Tests.ps1
```

The icon test reads the already built package, validates all ICO frames and the embedded resource,
extracts Explorer Shell icons, and checks both real EXE windows' native icons.
It uses an isolated profile with background monitoring disabled and diagnostic targets limited to `127.0.0.1:9`.
Evidence is written outside the repository. Optional `-CaptureDesktop` also captures the taskbar and Alt+Tab;
it briefly sends Alt+Tab and should only be used during an interactive acceptance session.

The runtime test deliberately compiles the real host runtime from a supplied checkout. CI checks out AppShelf 2.24.3 revision `c8edc77b5a9706e9b2f7a35a0c9a9deb2b4e3c46` solely for that compatibility test; build and independent unit tests do not depend on the host repository. Tests use temporary profiles with monitoring off, not the user's Windows network settings.

## Installation

Requires a compatible AppShelf build with plugin API v1. Install the package in the plugin manager and confirm trust, then enable it. AppShelf beta.3 migration is being developed to transfer the legacy settings and indicator selection on first explicit enable. Until that host transition is available, avoid simultaneous built-in and plugin monitoring of the same targets.

## Provenance and status

Extracted from AppShelf commit [`76fee373`](https://github.com/mishakov1c/app_shelf/commit/76fee373e94679c682a4d440076915338b872055). The network implementation and manifest are unchanged; only paths, standalone build, test wiring and documentation are adapted. The upstream GPL-3.0 license is retained. The initial package is a development build, not a published release.

## Releases

Normal pushes only build, test and upload CI artifacts. An explicitly approved commit on `main` with exact subject `release-preview: NetworkPlugin X.Y.Z` publishes a prerelease; `release: NetworkPlugin X.Y.Z` publishes a stable release and marks it Latest. Publication requires successful build, offline regression (including the embedded icon), monitor/UI and runtime/backup tests. The manifest and executable versions must match. Both channels use tag `vX.Y.Z`. Ordinary development commits do not publish releases.

Version 1.0.1 adds the public GitHub Releases update source. Install it manually over 1.0.0; subsequent newer previews can be checked in the AppShelf plugin manager. This release does not change network monitoring or settings.

Version 1.0.2 Preview migrates diagnostics and settings windows to standalone WPF with AppShelf dark styles, including dark title bars where supported by Windows, dropdowns and scrollbars. Network probes and settings format are unchanged. The update source from 1.0.1 is retained; select the eligible preview in the plugin manager to update. Visual acceptance and real online update from 1.0.1 were confirmed by the user on 2026-11-27; the only remaining remark was the missing app icon.

Version 1.0.3 adds the official globe icon to the EXE and both WPF windows. Local Windows acceptance by the agent confirmed title bars, taskbar, Alt+Tab and Explorer; user approved stable publication before final visual confirmation on the released package. Network behavior and update source are unchanged.
