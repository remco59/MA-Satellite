# ma-satellite

A lightweight Windows app that [Music Assistant](https://www.music-assistant.io/) (MA) can play to: a native Sendspin player with a tray mini-player, Windows media integration (media keys, volume flyout), a local now-playing API for desktop widgets such as Lively, and library browsing.

The full plan, decisions and milestones live in [`docs/PLAN.md`](docs/PLAN.md). Implementation notes that deviate from or extend the plan are in [`docs/NOTES.md`](docs/NOTES.md).

## Status

| Milestone | State |
|---|---|
| M0 Skeleton (tray, logging, settings, MSIX, CI) | in progress, see PR |
| M1 Player core | in progress |
| M2 Mini-player + Windows integration | planned |
| M3 Local API + Lively widget | planned |
| M4 Library browsing | planned |

## Layout

```
src/MaSatellite.Core      Sendspin protocol, clock sync, decoders, audio pipeline (no UI deps)
src/MaSatellite.Host      Settings, logging, app orchestration, local API (no Windows deps, unit-testable)
src/MaSatellite.Windows   CoreAudio/WASAPI, master volume, SMTC, tray icon, autostart
src/MaSatellite.App       WinUI 3 app, single-project MSIX
src/MaSatellite.MaApi     Client for MA's own WebSocket API (library browsing)
widgets/lively            HTML/JS now-playing widget
tests/*                   xUnit tests
```

## Build

Requirements: .NET 8 SDK. The WinUI app and the MSIX need Windows (Visual Studio 2022 or Build Tools with the "Windows application development" workload).

```powershell
# cross-platform libraries + tests (also works on Linux/macOS with MaSatellite.Linux.slnf)
dotnet test MaSatellite.Tests.slnf
```

## Install (dev build)

CI publishes `ma-satellite-msix` as a workflow artifact containing the signed `.msix`, the public certificate `MaSatellite.Dev.cer` and `Install-Dev.ps1`.

1. Download the artifact and unzip it.
2. In an **elevated** PowerShell (needed once, to trust the dev certificate):
   ```powershell
   .\Install-Dev.ps1 -Package .\MaSatellite.App_0.1.0.0_x64.msix -Certificate .\MaSatellite.Dev.cer
   ```
3. The icon appears in the notification area; the app starts with Windows (toggle it in the tray menu).

To build locally instead: `scripts/New-DevCertificate.ps1`, then `scripts/Build-Msix.ps1`.

Logs: `%LOCALAPPDATA%\MaSatellite\logs` (packaged apps see this redirected below `%LOCALAPPDATA%\Packages\<package>\LocalCache\Local\MaSatellite`). Settings: `settings.json` next to it.

## Uninstall

Settings → Apps → ma-satellite → Uninstall (or `Get-AppxPackage MaSatellite | Remove-AppxPackage`). Optionally remove the dev certificate from *Trusted People*.
