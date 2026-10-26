# ma-satellite — Plan

A lightweight Windows app that Music Assistant (MA) can play to, with a native mini-player, Windows media integration, a now-playing API for desktop widgets, and (later) library browsing.

> Status: agreed · 2026-10-26 · Owner: Remco

## Goals

- Replace Squeezelite on Windows with a native, nicer satellite player.
- Show up in MA automatically and play in sync with other MA players.
- MA's volume slider **is** the Windows master volume, in both directions.
- Integrate with Windows: tray, media keys, volume flyout, lock screen.
- Expose now-playing info locally so desktop widgets (Lively) can use it.
- Browse and queue the MA library from the app (M4).

## Non-goals (for now)

- Running as an MA *server* or a source/line-in.
- macOS/Linux support.
- Microphone / voice satellite features.

## Context

| Item | Value |
|---|---|
| MA server | HA add-on on Raspberry Pi 5, `192.168.0.98` (MA 2.10.x) |
| MA web/API | `http://192.168.0.98:8095` |
| Protocol | Sendspin (MA's native protocol; technical preview) |
| Discovery | mDNS `_sendspin._tcp.local.`, WebSocket on `/sendspin`, default port 8928 |
| Target PC | Windows 11, x64 |

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Protocol | **Sendspin** | Native to MA: sync, metadata, artwork, controller role |
| Stack | **C# / .NET + WinUI 3** | Native CoreAudio, SMTC and tray; small footprint |
| Audio output | **WASAPI (shared mode)** | Low latency, device selection, works with system mixer |
| Volume | **Windows master volume** (`IAudioEndpointVolume`) | Explicit choice; slider = system volume. Note: also affects other apps |
| Delivery | Milestones, each usable on its own | Library browsing is as big as the player itself |
| Name | **ma-satellite** | Repo, package and namespace (`MaSatellite.*`) |
| Packaging | **MSIX from M0** | Clean install/uninstall, StartupTask for autostart, package identity for Windows APIs |
| Autostart | **On by default** | MSIX `StartupTask`, toggle in tray/settings |
| MA library access | **Long-lived MA token** | Stored encrypted (DPAPI) in app data; never logged |

## Architecture

```
┌──────────────────────── ma-satellite (one process) ────────────────────────┐
│                                                                             │
│  UI (WinUI 3)            Host                          Core                 │
│  ─────────────           ──────────────────            ──────────────────── │
│  Tray mini-player   ◄──► App state / settings   ◄──►   Sendspin client      │
│  Library views           SMTC (media keys etc.)        ├─ mDNS advertise    │
│  Settings                Master-volume binding         ├─ WS server/client  │
│                          Local API (REST + WS)         ├─ clock sync        │
│                                │                       ├─ decode (PCM/FLAC) │
│                                │                       └─ WASAPI output     │
└────────────────────────────────┼────────────────────────────────────────────┘
                                 ▼
                    Lively widget / other local consumers
```

**Projects (solution layout)**

- `src/MaSatellite.Core`: Sendspin protocol, clock sync, decoding, audio pipeline. No UI deps; unit-testable.
- `src/MaSatellite.Windows`: CoreAudio (devices, master volume), SMTC, mDNS, autostart.
- `src/MaSatellite.App`: WinUI 3 app, tray, views, local API host.
- `src/MaSatellite.MaApi`: client for MA's own WebSocket API (M4).
- `widgets/lively`: HTML/JS now-playing widget.
- `tests/*`: xUnit unit + integration tests (integration against a real MA instance, opt-in).

**Key rule:** the protocol layer sits behind interfaces (`ISendspinTransport`, `IPlayerRole`, …), because Sendspin is still in technical preview.

## Milestones

### M0: Skeleton
- Solution + project layout as above, `Directory.Build.props`, nullable on, warnings as errors.
- CI (GitHub Actions): build + unit tests on `windows-latest`.
- Single-instance app with a tray icon and an Exit menu.
- Structured logging (file + debug), log folder in `%LOCALAPPDATA%`.
- Settings file (JSON): player name, output device, MA host, autostart.
- MSIX packaging (single-project MSIX), self-signed dev certificate for local installs; CI builds the `.msix` as an artifact.
- Autostart via MSIX `StartupTask`, enabled by default, toggle in the tray menu.

**Done when:** the MSIX installs, the app starts to the tray (also after login), logs, persists settings, and CI is green.

### M1: Player core (replaces Squeezelite)
- mDNS advertisement + WebSocket endpoint; also support client-initiated connect to a configured host.
- Sendspin handshake (`client/hello`), player role, state reporting.
- Clock sync + buffered, timestamped playback (drift correction).
- Decoders: PCM + FLAC (Opus later).
- WASAPI output, device picker, handles device unplug/switch.
- Volume: bind MA volume ↔ Windows master volume, with change notifications so system changes update MA.
- Mute ↔ Windows mute.

**Done when:** it shows up in MA, plays a 1-hour stream without dropouts, plays in sync in a group with another Sendspin player, and the volume stays in sync both ways.

### M2: Mini-player + Windows integration
- Metadata + artwork roles: title, artist, album, cover, progress.
- Tray flyout mini-player: artwork, play/pause/skip, seek bar, volume, output device.
- SMTC: media keys, Windows volume flyout card, lock screen.
- Controller role: play/pause/next/previous to MA.

**Done when:** you can control everything from the tray and the media keys, and the flyout shows correct info.

### M3: Local API + Lively widget
- `GET /api/now-playing` (JSON), `GET /api/artwork`, and `WS /api/events` (state, progress, volume).
- Bound to `127.0.0.1` only; CORS limited to `localhost`.
- `widgets/lively`: now-playing widget; hook into the existing Lively media display.

**Done when:** the Lively widget updates live without polling.

### M4: Library browsing
- Connect to MA's API (`ws://192.168.0.98:8095/ws`) with a long-lived token, entered once in Settings and stored with DPAPI.
- Search, artists/albums/playlists, queue view.
- Play to this PC or any other MA player; transfer the queue.

**Done when:** you can find and start an album from the app without opening MA.

### Later
- Auto-update (App Installer file or GitHub Releases), proper code-signing certificate.
- Opus decoder, visualizer role.
- Multiple instances / per-device players.

## Testing

- **Unit:** protocol message parsing, clock-sync math, buffer/drift logic, volume mapping (fakes for audio/network).
- **Integration (opt-in):** connect to a real MA (`MA_HOST` env var); play a known track and assert state transitions.
- **Manual checklist per milestone:** listed in each milestone's "Done when".

## Risks

| Risk | Mitigation |
|---|---|
| Self-signed MSIX needs the cert trusted before install | Documented one-time cert import; real signing cert later |
| Sendspin spec changes (tech preview) | Protocol behind interfaces; pin to MA version; integration tests |
| Clock sync / glitches | Start with a generous buffer; measure drift; tune later |
| Master volume affects other apps | Accepted (explicit choice); add an "app volume only" option later if it annoys |
| Borrowing code from Whirtle (open-source Sendspin client) | Check its license before copying; prefer reading the spec |
| Windows Firewall blocks the inbound WS | Declare the firewall rule in the MSIX manifest; client-initiated mode as fallback |

## Resolved questions

1. **Name:** `ma-satellite`.
2. **MA token (M4):** yes, long-lived token for the app.
3. **Packaging:** MSIX from the start.
4. **Autostart:** on by default.

## M0 tickets

1. **Create solution + projects:** layout as in *Architecture*, shared build props, .editorconfig.
2. **CI workflow:** GitHub Actions on `windows-latest`: restore, build, test.
3. **Tray host:** single instance, tray icon, menu (Open / Settings / Exit).
4. **Logging:** file + debug sinks, rolling logs in `%LOCALAPPDATA%\MaSatellite\logs`.
5. **Settings:** JSON settings with defaults + load/save + validation tests.
6. **MSIX packaging:** single-project MSIX, dev cert, CI artifact, install instructions.
7. **Autostart:** `StartupTask` in the manifest, on by default, tray toggle.
8. **README:** what it is, how to build/install, link to this plan.

## References

- Sendspin provider docs: https://www.music-assistant.io/player-support/sendspin
- Sendspin protocol spec + reference implementations: linked from the page above
- Whirtle (Windows Sendspin client, .NET): https://github.com/whirtle/client
- MA docs: https://www.music-assistant.io/
