# NetworkPlugin 1.1.1 for AppShelf

Independent Windows WPF network diagnostics plugin for [AppShelf](https://github.com/mishakov1c/app_shelf), plugin API v1.
Development plan: [ROADMAP.md](ROADMAP.md). Licensing: [NOTICE.md](NOTICE.md), [LICENSE](LICENSE) (GPL-3.0).

## Features

- Two configurable ICMP / HTTPS / TCP checks, default ICMP + HTTPS with TCP fallback.
- Eight-second monitoring and the original `network1`, `network2`, `route` indicators.
- One diagnostics window: connectivity, active interfaces, DNS, WinINET proxy and Windows route.
- One settings window, with a separate **VPN / TUN** section.
- Optional VPN module, **off by default**, with explicit adapter and process selection.
- Optional fourth `tun` indicator; its action opens the same diagnostics window.
- Guarded, separately confirmed recovery steps and local proxy restoration.
- One plugin ID (`appshelf.network`), one manifest, one `NetworkPlugin.exe` and one plugin process.
  Explicit UAC operations launch the appropriate Windows utility, not another plugin.

## VPN settings and safety

Open the existing settings window and enable **Включить модуль VPN**. Only then are adapters and
readable running programs enumerated. Select your VPN adapters by their stable Windows IDs and
programs by full local EXE path, not by name patterns. Refresh the lists to discover newly started programs. Version 1.1.1 adds independent, case-insensitive adapter and program search fields, result/selection counts, and a clear button. Program names appear above their full paths. Filtering is local and never clears hidden selections or starts discovery; searches are not persisted.
Nothing is automatically selected; missing saved entries remain selectable/removable.
System, host and plugin processes are excluded. Up to 16 adapters and 16 EXE paths may be selected.

The fourth indicator is separately optional. Enable **Предлагать четвёртый индикатор VPN / TUN**,
save, then add it through AppShelf's standard indicator picker. With this option off,
VPN background polling stops; manual VPN diagnostics remain available while the module is enabled.
Disabling the module hides VPN recovery and the fourth descriptor, stops its polling, and excludes
its diagnostics. It retains local selections for later use and does not change Windows.

The indicator distinguishes an absent, inactive, unknown or ambiguous selection, a Windows-selected
IPv4 route, and an Up adapter with no matching sampled route. It checks Windows' interface choice
for `1.1.1.1` and `8.8.8.8`, without transmitting VPN test packets. Split routing and IPv6 may differ.
**Adapter/route state is not proof of VPN connectivity or a client application's switch state.**

Opening or refreshing the common diagnostics still performs NetworkPlugin's existing configured
network checks. The VPN appendix only reads local state. Proxy, DNS and connectivity sections are
shared, not repeated in a separate window.

### Recovery

Recovery never runs on launch, handshake, timer tick, indicator refresh or settings save.
Every modifying step requires an explicit confirmation, defaulting to **No**:

1. Stop only the selected EXE paths, confirming each PID. PID, start time and EXE path are rechecked.
2. Disable only selected Up adapters, confirming each one. ID, name, index and state are rechecked;
   Windows requests UAC for `netsh`. Do not select a physical adapter unless you intend to disconnect it.
3. Temporarily disable the current user's manual WinINET proxy only when every configured endpoint
   is loopback, no PAC is present, and the expected server and enabled state still match.
   Remote, mixed, malformed and PAC configurations are not modified.
4. Run DNS and direct/system-proxy HTTPS checks; offer `ipconfig /flushdns` with UAC only after DNS failure.
   DNS server addresses are never changed. Connectivity is checked again afterwards.

Cancellation of UAC or an error stops subsequent recovery steps. Completed actions remain in the result.
There is no automatic network reset, adapter reenabling or process restart.
**VPN, internet access and RDP may be interrupted.** An active TUN is normal during VPN use.
The plugin process is not a sandbox.

**Вернуть прокси…** is available after a confirmed proxy disable during the same plugin process session,
including after closing/reopening the diagnostics window. Restoration requires confirmation and the same
proxy guards. After plugin restart, use Windows settings if needed; the original server address is retained.
Disabling the module does not undo prior system actions.

## Compatibility, backup and updates

Version 1.1.0 adds optional fields to the existing `network-settings.json`. Existing 1.0.3 files are
read without rewriting them or enabling VPN. The original network fields, indicator IDs, command `open`,
plugin ID, entry point and GitHub update repository are unchanged.

`network-settings.json` remains the only backup-allowlisted file, now including VPN selections.
AppShelf backs it up only after explicit consent. It contains local program paths and adapter IDs;
do not commit or publish user settings or exported profiles.
Update installation preserves plugin data, archives the previous package through AppShelf, and leaves
the new version disabled until explicitly enabled. The archived 1.0.3 can read the additive settings.

## Build

Double-click `Build-Plugin.cmd`, or use Windows PowerShell 5.1 / .NET Framework 4.8:

```powershell
.\Build-Plugin.cmd --no-open --no-pause
```

Output: `dist/NetworkPlugin-1.1.1.asplugin`.
Repeated builds into the same directory atomically replace the package.
The build does not install/start the plugin, access a user profile, or require AppShelf sources or internet.
License and provenance notices are included under `bin/` alongside the single EXE.

### Icon

The original globe and WPF theme are retained. `Assets/NetworkPlugin.Source.png` is the approved
original; `NetworkPlugin.png` and `NetworkPlugin.ico` are derived assets.
The ICO contains 32-bit frames at 16, 20, 24, 32, 48, 64, 128 and 256 pixels,
embedded as both the Windows EXE icon and a frozen managed resource shared by both windows.
Regenerate with `powershell.exe -NoProfile -ExecutionPolicy Bypass -File Assets/Build-Icon.ps1`.

## Tests

Run using Windows PowerShell 5.1 `-NoProfile -STA -ExecutionPolicy Bypass -File`:

| Script in `tests/` | Coverage |
| --- | --- |
| `NetworkPlugin.Tests.ps1` | Two consecutive builds, manifest/update source, icon resource, targets, atomic settings, original indicators |
| `NetworkMonitor.Tests.ps1` | Offline monitor generations, WPF settings, discovery fixtures, VPN-only monitoring, indicator transitions, complete disable |
| `VpnModule.Tests.ps1` | Memory-only recovery, confirmations, proxy guards, identity checks, cancellation, route states, legacy settings |
| `NetworkRuntime.Tests.ps1 -HostSourceDirectory .test-host` | Real host runtime, lifecycle, backup consent/allowlist/restore |
| `NetworkUpgrade.Tests.ps1 -HostSourceDirectory .test-host -BaselinePackage dist/NetworkPlugin-1.0.3.asplugin` | Actual local upgrade, rejected hash/decline, previous package archive, VPN off/on, settings backup and archived rollback |
| `NetworkIcon.Tests.ps1` | Built EXE, Windows Shell, singleton windows, icons, report sections and layout at three sizes |
| `NetworkIcon.Tests.ps1 -VpnEnabled` | Same real EXE checks with VPN on and UI Automation decline of recovery |
| `NetworkIcon.Tests.ps1 -Utf8ParentInput` (also with `-VpnEnabled`) | All icon/window checks with a BOM-emitting UTF-8 parent, reproducing the Windows CI encoding boundary |

Tests isolate profiles and never approve real recovery. Network probes in actual-window tests are
limited to `127.0.0.1:9`; recovery/connectivity tests use in-memory substitutes.
Adapter IDs, names and program paths in fixtures are synthetic, not captured from a user machine.
Supply a host checkout at `.test-host` (or another directory) for runtime/upgrade tests.
Window screenshots/evidence stay outside the repository. They can contain local adapter/program details;
do not publish them. Optional `-CaptureDesktop` captures other desktop windows and sends Alt+Tab:
use it only during an interactive acceptance session.

CI pins the host revision for compatibility and the accepted 1.0.3 baseline for upgrade tests.
The host pin is the exact AppShelf 2.24.3 release commit `c8edc77b5a9706e9b2f7a35a0c9a9deb2b4e3c46`.
No AppShelf core modification is needed. Local tests do not constitute CI or real destructive-operation acceptance.

The real-process test launches .NET Framework's redirected stdin with BOM-less UTF-8 and
immediately restores the parent console encoding. Otherwise `Process.Start` can emit an encoding
preamble before the first binary frame when the parent uses UTF-8 with BOM. Frame validation is
unchanged. IPC failures include the request method/ID, process exit status and asynchronously
drained stderr, not the request payload or local profile.

## Publication

Ordinary commits build/test and upload CI artifacts only. The existing stable/preview workflow is retained:
an explicitly approved `main` commit with exact subject `release: NetworkPlugin X.Y.Z` publishes stable,
or `release-preview: NetworkPlugin X.Y.Z` publishes a preview, after all checks pass.
Manifest and executable versions must agree. **Version 1.1.0 was published as Preview; 1.1.1 adds search and is approved for Preview publication.** The reviewed integration passed push and PR Windows CI, including the actual 1.0.3 upgrade and recovery checks with test doubles. Real system-changing recovery operations have not been accepted; stable publication requires separate acceptance.
