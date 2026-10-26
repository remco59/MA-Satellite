# ma-satellite

A lightweight Windows app that [Music Assistant](https://www.music-assistant.io/) (MA) can play to: a native Sendspin player with a tray mini-player, Windows media integration (media keys, volume flyout), a local now-playing API for desktop widgets such as Lively, and library browsing.

The full plan, decisions and milestones live in [`docs/PLAN.md`](docs/PLAN.md). Implementation notes that deviate from or extend the plan are in [`docs/NOTES.md`](docs/NOTES.md).

## Status

| Milestone | State |
|---|---|
| M0 Skeleton (tray, logging, settings, MSIX, autostart, CI) | done |
| M1 Player core (Sendspin, clock sync, FLAC/PCM, WASAPI, master volume) | done, needs your manual run (`docs/TESTING.md`) |
| M2 Mini-player + Windows integration (flyout, SMTC, media keys) | done, needs your manual run |
| M3 Local API + Lively widget | done |
| M4 Library browsing (search, queue, transfer) | done, needs your manual run |

Everything that can be tested without Windows hardware is covered by automated tests; audio output, the tray, SMTC and the WinUI windows are verified to build in CI but must be checked by hand.

> **Protocol note.** ma-satellite speaks both the current Noise-encrypted Sendspin protocol (with pairing) and the older plaintext one that Music Assistant 2.10 still accepts while *Allow legacy clients* is on. *Automatic* mode (default) tries encrypted first. See `docs/NOTES.md`.

## Layout

```
src/MaSatellite.Core      Sendspin protocol, clock sync, decoders, audio pipeline (no UI deps)
src/MaSatellite.Host      Settings, logging, app orchestration, local API (no Windows deps, unit-testable)
src/MaSatellite.Windows   CoreAudio/WASAPI, master volume, SMTC, tray icon, autostart
src/MaSatellite.App       WinUI 3 app, single-project MSIX (also builds unpackaged for the .exe installer)
src/MaSatellite.MaApi     Client for MA's own WebSocket API (library browsing)
installer                 Inno Setup script for the .exe installer
widgets/lively            HTML/JS now-playing widget
tests/*                   xUnit tests
```

## Build

Requirements: .NET 8 SDK. The WinUI app and the MSIX need Windows (Visual Studio 2022 or Build Tools with the "Windows application development" workload).

```powershell
# cross-platform libraries + tests (also works on Linux/macOS with MaSatellite.Linux.slnf)
dotnet test MaSatellite.Tests.slnf
```

## Install

### Option A: `.exe` installer (simplest)

Download `ma-satellite-setup-<version>.exe` from the latest [GitHub Release](../../releases/latest) (or, for a branch build, from the `ma-satellite-installer` workflow artifact).

1. Run it. It installs for the current user without admin rights. Choose *Install for all users* if you want the setup to add the Windows Firewall rule for Music Assistant to connect in (TCP 8928, private networks only); otherwise Windows asks the first time, or use *Connect* mode.
2. The icon appears in the notification area; the app starts with Windows (toggle it in the tray menu).
3. Uninstall via Settings → Apps. It offers to remove your settings and logs too.

The installer is not code-signed yet, so Windows SmartScreen shows "unknown publisher": *More info → Run anyway*. Do not install both the `.exe` and the MSIX on one PC.

To build it locally (needs Inno Setup 6): `scripts/Build-Installer.ps1`.

### Option B: MSIX (dev build)

The release also carries the signed `.msix`, `MaSatellite.Dev.cer` and `Install-Dev.ps1`; the steps are:

CI publishes `ma-satellite-msix` as a workflow artifact containing the signed `.msix`, the public certificate `MaSatellite.Dev.cer` and `Install-Dev.ps1`.

1. Download the artifact and unzip it.
2. In an **elevated** PowerShell (needed once, to trust the dev certificate):
   ```powershell
   .\Install-Dev.ps1 -Package .\MaSatellite.App_0.1.0.0_x64.msix -Certificate .\MaSatellite.Dev.cer
   ```
3. The icon appears in the notification area; the app starts with Windows (toggle it in the tray menu).

To build locally instead: `scripts/New-DevCertificate.ps1`, then `scripts/Build-Msix.ps1`.

## Use

1. Start the app. It advertises itself on the network; in Music Assistant the player *ma-satellite* (named after your PC) appears under Players. Left-click the tray icon for the mini-player, right-click for the menu.
2. The volume slider in Music Assistant **is** the Windows master volume of the selected output device, in both directions (so it also affects other apps).
3. For library browsing enter your Music Assistant host and a long-lived token in *Settings* (the token is stored encrypted for your Windows account, never logged).
4. For desktop widgets see [`widgets/lively`](widgets/lively/README.md); the local API is documented there.

More: [`docs/TESTING.md`](docs/TESTING.md) (checklists, troubleshooting), [`docs/NOTES.md`](docs/NOTES.md) (decisions).

Logs: `%LOCALAPPDATA%\MaSatellite\logs` (packaged apps see this redirected below `%LOCALAPPDATA%\Packages\<package>\LocalCache\Local\MaSatellite`). Settings: `settings.json` next to it.

## Uninstall

Settings → Apps → ma-satellite → Uninstall (or `Get-AppxPackage MaSatellite | Remove-AppxPackage`). Optionally remove the dev certificate from *Trusted People*.

## Releasing

Set `<Version>` in `Directory.Build.props` (and the manifest's `Identity Version`), merge, then push a matching tag: `git tag v0.1.0 && git push origin v0.1.0`. CI builds, tests and attaches the installer and MSIX files to a new GitHub Release (it fails if the tag and the version disagree).
