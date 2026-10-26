# Implementation notes

Decisions made while implementing [PLAN.md](PLAN.md) where the plan was silent or reality differed. Newest first.

## Protocol version (important)

The plan assumes plain `client/hello` JSON over WebSocket. The Sendspin spec on `main` (as of 2026-10) has since added a mandatory Noise (`KKpsk2`) encrypted handshake, pairing and a changed `client/hello` flow. Music Assistant 2.10.8 supports both: its Sendspin provider setting **Allow legacy clients** (on by default, documented as temporary) still accepts unencrypted legacy clients.

- The first transport implemented is the **legacy plaintext** one (the spec as of commit `8bfa0c5`, i.e. just before "Add encryption support"), because that is what the plan describes and what MA accepts today.
- The protocol layer sits behind `ISendspinTransport`, exactly so a Noise transport can be added later without touching the player. When MA removes legacy support, the Noise transport becomes mandatory.

## Layout deviations

- Added `MaSatellite.Host` (settings, logging, orchestration, local API). Keeps everything that is not WinUI testable on any OS, and keeps `MaSatellite.App` thin.
- mDNS advertise/discovery lives in `MaSatellite.Core` (it is cross-platform and unit-testable), not `MaSatellite.Windows`.
- The tray icon is a small Win32 `Shell_NotifyIcon` wrapper in `MaSatellite.Windows` instead of a third-party XAML control: fewer dependencies, native context menu.

## What was verified how

- Verified by automated tests in CI/Linux: protocol parsing, time filter (spec algorithm), FLAC (bit-exact against ffmpeg-encoded fixtures in every stereo mode and block size), the playback engine (start timing, clock skew, gaps, late audio, resampling), the whole player against a fake Sendspin server over real sockets, the local API, the Music Assistant client against a fake server, settings, DPAPI store (Windows runner), SMTC button mapping.
- Verified to *build* only: WASAPI output, master volume binding, tray icon, SMTC publishing, WinUI windows, MSIX packaging/signing. They need a hands-on run (`docs/TESTING.md`).
- Not verified: behaviour against a real Music Assistant (the opt-in integration test is there for that), Opus (not implemented; not advertised, so MA never sends it).

## Design notes

- **Sync model.** Each decoded chunk keeps its server timestamp; the read position is compared with `LocalToServer(playoutTime + staticDelay)` for every device buffer. Differences below 30 ms are corrected by a slightly varied resampling ratio (max ±0.5 %), larger ones by skipping audio or inserting silence. Gaps and overlaps in the server timeline fall out of the same rule.
- **Output.** The WASAPI render loop is our own (event-driven, shared mode, device mix format, ~60 ms kept queued) so the exact time a frame becomes audible is known.
- **Arbitration.** One server at a time, decided after `server/hello` using `connection_reason` and the persisted last-played server, as the spec describes.
- **Local API.** `HttpListener` on `127.0.0.1` only, no admin rights needed; refuses foreign `Origin` and `Host` headers; `POST /api/command` needs `application/json` (forces a CORS preflight that foreign origins fail).
- **Secrets.** The MA token is encrypted with DPAPI (current user) in `secrets/ma-token.bin` and never logged.
- **Firewall.** The MSIX declares an inbound rule for TCP 8928 on *private* networks only; on public networks use *Connect* mode.

## Known gaps / next steps

- Noise-encrypted Sendspin transport + pairing (see top). This is the main thing that will be needed once Music Assistant drops legacy support.
- Opus decoder, visualizer role, per-device multiple instances (all listed under "Later" in the plan).
- Auto-update and a real code-signing certificate (plan: "Later").
