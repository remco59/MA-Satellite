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
