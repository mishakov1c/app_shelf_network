# NetworkPlugin for AppShelf

Independent network diagnostics plugin for [AppShelf](https://github.com/mishakov1c/app_shelf), using plugin API v1.

- Two configurable checks: default ICMP + HTTPS with TCP fallback, ICMP, HTTPS, TCP.
- Eight-second background monitoring, three optional bottom-panel indicators.
- Manual DNS/HTTPS diagnostics, Windows route, active adapters and proxy report.
- Separate settings in the host-provided plugin data directory; `network-settings.json` is the only file allowed for AppShelf backups, after explicit user consent. No writes to Windows network settings.
- Happ recovery is not included.

## Build

On Windows with .NET Framework 4.8, run in Windows PowerShell 5.1:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build.ps1
```

Output: `dist/NetworkPlugin-1.0.0.asplugin`. The build uses the local API v1 protocol source and does not require AppShelf.exe, a host source checkout or internet access.

## Tests

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tests/NetworkPlugin.Tests.ps1
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tests/NetworkRuntime.Tests.ps1 -HostSourceDirectory C:\path\to\app_shelf
```

The second test deliberately compiles the real host runtime from a supplied checkout. CI checks out the exact tested upstream revision `76fee373e94679c682a4d440076915338b872055` solely for that compatibility test; build and independent unit tests do not depend on the host repository. Tests use temporary profiles with monitoring off, not the user's Windows network settings.

## Installation

Requires a compatible AppShelf build with plugin API v1. Install the package in the plugin manager and confirm trust, then enable it. AppShelf beta.3 migration is being developed to transfer the legacy settings and indicator selection on first explicit enable. Until that host transition is available, avoid simultaneous built-in and plugin monitoring of the same targets.

## Provenance and status

Extracted from AppShelf commit [`76fee373`](https://github.com/mishakov1c/app_shelf/commit/76fee373e94679c682a4d440076915338b872055). The network implementation and manifest are unchanged; only paths, standalone build, test wiring and documentation are adapted. The upstream GPL-3.0 license is retained. The initial package is a development build, not a published release.
