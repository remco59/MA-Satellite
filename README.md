# MA Satellite

**A Windows desktop player and companion app for [Music Assistant](https://www.music-assistant.io/).** Stream music to your PC using Sendspin, browse your Music Assistant library, control other players, and see what's playing without leaving Windows.

Built with **.NET 8**, **WinUI 3**, and **WASAPI**. The **Dark Aurora** interface brings a library, personalized home screen, and persistent playback controls to the desktop; a compact mini-player is also available from the system tray.

> **Release status:** This README describes the code on `main`. The latest [published release](https://github.com/remco59/MA-Satellite/releases/latest) may be behind `main` while newer UI changes are being packaged. Check the release version and notes before expecting every feature below in a downloaded installer. Successful [CI builds](https://github.com/remco59/MA-Satellite/actions/workflows/ci.yml) also publish build artifacts.

## Features

| Area | What it does |
| --- | --- |
| **Music Assistant player** | Exposes your Windows PC as a Sendspin playback target, with clock-synchronized PCM audio over WASAPI. |
| **Desktop library** | Browse albums, artists, tracks, and playlists; search Music Assistant and its providers; open albums and playlists. |
| **Library views and sorting** | Switch between grid and list layouts for albums, artists, tracks, and playlists. Sort by name, date added, last played, or play count; albums also offer release year. Unsupported server-side sorts fall back to default order. |
| **Personalized Home** | Continue recent listening; browse albums, frequently played playlists and tracks, time-of-day suggestions from your own playlists/history, and similar artists when the provider supports them. All recommendations use Music Assistant data, not fabricated content. |
| **Playback control** | Play now, play next, add to queue, choose a target player, control playback and seeking, and transfer a queue between players. A persistent player footer includes track info, transport, volume, shuffle/repeat, and queue access. |
| **Windows integration** | Tray mini-player, media-key and System Media Transport Controls (SMTC) integration, output-device selection, and optional startup with Windows. |
| **Desktop widgets** | Loopback-only now-playing HTTP/WebSocket API and a ready-to-use [Lively Wallpaper widget](widgets/lively/README.md). |
| **Connection security** | Encrypted Sendspin with pairing support, plus optional legacy compatibility for older servers. Long-lived credentials are encrypted for the Windows user. |

> **Compatibility note:** MA Satellite currently **advertises PCM audio**, not FLAC, for reliable playback with real Music Assistant servers. The code includes a FLAC decoder, but FLAC streaming is not enabled for negotiation yet. Opus is not implemented.

## Download and install

**Requirements:** Windows 10/11 on an x64 PC, a running Music Assistant server with its Sendspin provider enabled, and network access between the two devices. Published installers are built for x64.

### Recommended: Windows `.exe` installer

1. Download `ma-satellite-setup-<version>.exe` from [Releases](https://github.com/remco59/MA-Satellite/releases/latest).
2. Run the installer. A **per-user installation** does not need administrator rights.
3. For a shared-PC installation, optionally choose **Install for all users**. That option can also add an inbound Windows Firewall rule for Sendspin on TCP **8928**, limited to **Private** networks.
4. Start **ma-satellite** and find its icon in the system tray (notification area).

The installer is currently **unsigned**: Windows SmartScreen may identify it as an unknown publisher. Verify that you downloaded the expected installer from this repository before deciding whether to proceed. Do **not** install the EXE and MSIX variants side by side.

### Alternative: MSIX development package

The release or a successful CI run also provides an `.msix`, `MaSatellite.Dev.cer`, and `Install-Dev.ps1`. Unzip all three into the same folder, then open **PowerShell as Administrator** to install and trust the development certificate:

```powershell
.\Install-Dev.ps1 -Package .\MaSatellite.App_<version>.0_x64.msix -Certificate .\MaSatellite.Dev.cer
```

Replace `<version>.0` with the **actual MSIX filename** from the download (for example, `0.1.1.0`). Trust the development certificate only if you trust this project's build. For a normal installation, prefer the EXE.

## First-time setup

### 1. Connect the Windows player

1. Start MA Satellite. In **Settings → Advanced · Network & diagnostics**, the default connection mode is **Advertise on the network**.
2. In Music Assistant, enable/configure the **Sendspin** player provider. Find your PC (named after your Windows machine unless changed in MA Satellite settings) under **Settings → Players**, and enable/approve it if necessary.
3. Select the PC as a playback target in Music Assistant and play a track. You can left-click the MA Satellite tray icon for the mini-player or right-click it to open the library and settings.
4. If you see playback metadata but hear nothing, open **Settings → Player & output** and select your actual speakers or headphones instead of **System default**. Check the Windows output level as well.

**If discovery fails:** Use a Private Windows network, permit the inbound connection on TCP 8928, or add your PC's IP to the Sendspin provider's **Manual IP addresses** in Music Assistant. Alternatively, switch MA Satellite to **Connect to the Music Assistant host** and enter your server address.

### 2. Connect the music library

Sendspin playback and library browsing are separate connections. A player can appear in Music Assistant before the app's library view has been configured.

1. In Music Assistant, create a **long-lived access token** for your user.
2. Open MA Satellite **Settings → Music Assistant** and enter:
   - **Server address:** the Music Assistant hostname or IP address (without `http://`).
   - **Music Assistant web port:** `8095` by default; use your server's configured port.
   - **Library access token:** the long-lived token.
3. Save settings and use **Test connection**.
4. Open **Library** from the tray menu (or mini-player). Select **This PC** or another Music Assistant player using the target selector.

The token is stored encrypted using Windows DPAPI for your account, not in plain-text settings. Recommendations and library features depend on the content, listening history, and APIs available from your Music Assistant server and music providers; unsupported recommendation shelves are hidden.

### 3. Explore and control playback

- **Home:** Recently played, albums, your playlists, frequently played tracks, time-of-day selections, and similar artists where supported.
- **Library / Playlists:** Toggle **List** or **Grid** and use **Sort library**. View/sort choices are tracked separately per section during the app session.
- **Search:** Find music across the connected Music Assistant providers.
- **Now playing / Queue:** See the selected target's current track, control playback, play a queue item, or transfer the queue to another player.
- **Player footer:** Control playback and volume while navigating your library. For **This PC**, MA Satellite uses live Sendspin playback state; other players are controlled through the Music Assistant API.

**Volume behavior:** The MA Satellite player's volume is the **Windows master volume for the selected output device**. Changing it from Music Assistant also affects other applications using that device.

## Secure Sendspin and pairing

In **Settings → Playback & sync**, **Automatic** tries the encrypted Sendspin protocol first and can fall back to legacy plaintext for servers that still support it. You can choose **Encrypted only** or **Legacy only** explicitly. Legacy mode requires *Allow legacy clients* to be enabled on the Music Assistant server.

The **Security & pairing** section shows a pairing token, paired servers, and an option to disallow unpaired servers. Pairing provides a trusted connection to a specific server; encrypted but **unpaired** access does not provide the same server authentication. You may also regenerate the pairing token or forget a paired server.

**Settings → Playback & sync → Static delay** can compensate for audio output latency when grouping the PC with other players. See [testing and pairing checks](docs/TESTING.md) and [protocol details](docs/NOTES.md).

## Network ports

| Port | Used for | Exposure |
| --- | --- | --- |
| TCP **8928** | Incoming Sendspin player connections in **Advertise** mode | LAN; Private-network firewall rule when installed for all users |
| TCP **8927** | Music Assistant Sendspin server endpoint in **Connect** mode | Outgoing to your MA server |
| TCP **8095** | Music Assistant web/API connection for the library | Outgoing to your MA server; configurable |
| TCP **8929** | Local now-playing API and Lively widget | **127.0.0.1 only**; configurable |

Ports shown are defaults. You can change the player listening port and local API port under **Advanced · Network & diagnostics**. The local API intentionally rejects requests from untrusted origins/hosts.

## Lively Wallpaper now-playing widget
1. Download the artifact and unzip it.
2. In an **elevated** PowerShell (needed once, to trust the dev certificate):
   ```powershell
   .\Install-Dev.ps1 -Package .\MaSatellite.App_0.2.0.0_x64.msix -Certificate .\MaSatellite.Dev.cer
   ```
3. The icon appears in the notification area; the app starts with Windows (toggle it in the tray menu).

The included `widgets/lively` integration displays the current track and progress on your desktop:

1. Enable **Local API for desktop widgets** in MA Satellite settings (enabled by default).
2. In [Lively Wallpaper](https://rocksdanister.github.io/lively/), add `widgets/lively/index.html` as a wallpaper/widget.
3. If you changed the API port, adjust it in the widget's settings as well.

See the [widget guide](widgets/lively/README.md) for configuration and the HTTP/WebSocket endpoints. The default now-playing endpoint is `http://127.0.0.1:8929/api/now-playing`.

## Troubleshooting

| Problem | What to check |
| --- | --- |
| **PC does not appear in Music Assistant** | Sendspin provider enabled, network marked **Private**, firewall TCP 8928, and mDNS discovery. Try a manual IP or **Connect** mode. |
| **Music Assistant shows playback but there is no audio** | Select a connected device in **Settings → Player & output**, check mute/volume, and restart playback. See the [manual audio checklist](docs/TESTING.md). |
| **Music library is empty / cannot connect** | Set the MA server host, web port and **long-lived access token**; save and test the connection. |
| **No personalized recommendations** | The server may lack listening history, play-count sorting or similar-artist support, or your library/history may be empty. These sections disappear when unavailable. |
| **Player asks for approval or pairing** | Enable/approve the PC under Music Assistant's player settings; review the **Security & pairing** section. |
| **Music Assistant lists the PC twice** | Encrypted Sendspin identifies the player by its key. Remove any obsolete legacy player entry in Music Assistant. |
| **Audio is out of sync when grouped** | Adjust **Static delay**; Bluetooth devices can have substantial output latency. |
| **Port 8928 is already in use** | Choose another listening port in **Advanced · Network & diagnostics**, then update the network/firewall configuration. |

### Logs and settings

For an unpackaged EXE installation:

- Logs: `%LOCALAPPDATA%\MaSatellite\logs`
- Settings: `%LOCALAPPDATA%\MaSatellite\settings.json`

MSIX installs redirect the local app data directory under `%LOCALAPPDATA%\Packages\<package>\LocalCache\Local\MaSatellite`. Sensitive tokens and Sendspin keys are stored separately in Windows-user-encrypted secret files. For detailed verification and troubleshooting, follow [docs/TESTING.md](docs/TESTING.md).

## Development

**Prerequisites:** .NET **8 SDK**. Building the Windows app/MSIX requires Windows with Visual Studio 2022 or Build Tools and the **Windows application development** workload. Building the EXE installer also needs **Inno Setup 6**.

```powershell
# Cross-platform tests (Windows, macOS, Linux)
dotnet test MaSatellite.Linux.slnf

# Full unit tests (Windows)
dotnet test MaSatellite.Tests.slnf

# Publish a self-contained x64 Windows installer (Windows + Inno Setup 6)
.\scripts\Build-Installer.ps1

# Build and sign a development MSIX (Windows; password required by both scripts)
$password = Read-Host "Development signing password" -AsSecureString
.\scripts\New-DevCertificate.ps1 -PfxPassword $password
.\scripts\Build-Msix.ps1 -CertificatePfx .\artifacts\cert\MaSatellite.Dev.pfx -CertificatePassword $password
```

GitHub Actions builds the Windows app, runs tests, smoke-tests the installer, and uploads EXE/MSIX artifacts. Version-tagged builds publish a [GitHub Release](https://github.com/remco59/MA-Satellite/releases). A real Music Assistant integration test is available on an opt-in basis; see [docs/TESTING.md](docs/TESTING.md).

### Repository layout

```text
src/MaSatellite.App       WinUI 3 desktop interface and tray windows
src/MaSatellite.Core      Sendspin protocol, security, sync and audio pipeline
src/MaSatellite.Host      Settings, application orchestration, library and local API
src/MaSatellite.MaApi     Music Assistant WebSocket API client
src/MaSatellite.Windows   WASAPI, volume, SMTC, tray and Windows autostart
installer/                Inno Setup EXE packaging
scripts/                  Build, signing and installation scripts
widgets/lively/           Desktop now-playing widget
tests/                    Unit and integration tests
docs/                     Plan, implementation notes and testing instructions
```

## Uninstall

Uninstall **ma-satellite** from **Windows Settings → Apps**. The EXE uninstaller can optionally remove your settings and logs; its optional firewall rule is also removed. If you used the MSIX development package, you may additionally remove its development certificate from Windows *Trusted People* when it is no longer needed.

## Documentation

- [Testing, manual checklists and troubleshooting](docs/TESTING.md)
- [Implementation details and protocol decisions](docs/NOTES.md)
- [Original milestones and architecture plan](docs/PLAN.md)
- [Lively widget configuration and API](widgets/lively/README.md)
## Desktop UI acceptance test (Music Assistant connected)

The Windows CI builds and smoke-tests the installer, but it cannot see your personal Music Assistant server, speakers, or screen. After installing the v0.2.0 release, verify on your own PC:

1. Open the main window and resize it to about 1180×790, then below 900px wide. The sidebar, album cards and player should remain readable and the main navigation should work.
2. Open Home, Library (Albums/Artists/Tracks), Playlists, Search and Queue. Confirm that album/track cover art and empty/loading states display correctly.
3. Select **This PC**, start playback from Music Assistant and compare the mini-player with the main footer. Title, artwork, play/pause and progress should agree.
4. Play an album from Home or Library; check seek, previous/next, volume/mute and queue. For a remote Music Assistant player, also check shuffle and repeat when supported.
5. Unplug Music Assistant or enter an invalid server to inspect the error/retry state; reconnect and verify the library refreshes.

Use a screenshot of the main window and mini-player plus the log in `%LOCALAPPDATA%\MaSatellite\logs` when filing a visual or behavior bug.

## Releasing

Set `<Version>` in `Directory.Build.props` (and the manifest's `Identity Version`), merge, then push a matching tag (for example `git tag v0.2.0 && git push origin v0.2.0`). CI builds, tests and attaches the installer and MSIX files to a new GitHub Release (it fails if the tag and the version disagree).
