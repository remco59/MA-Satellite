# Testing

## Automated

| Layer | Where | Run |
|---|---|---|
| Protocol parsing, clock-sync math, buffer/drift logic, decoders (FLAC bit-exact against ffmpeg fixtures), volume mapping | `tests/MaSatellite.Core.Tests`, `tests/MaSatellite.Windows.Tests` | `dotnet test MaSatellite.Tests.slnf` |
| End to end: the real player against an in-process fake Sendspin server over real sockets (handshake, timing within a few ms, pitch, volume both ways, artwork, arbitration, reconnect) | `tests/MaSatellite.Core.Tests/SendspinServiceTests.cs` | same |
| Settings, local API (HTTP + WebSocket, origin/host checks), library controller against a fake Music Assistant | `tests/MaSatellite.Host.Tests`, `tests/MaSatellite.MaApi.Tests` | same |
| Lively widget in a real browser (mocked API) | `widgets/lively/test/widget.test.mjs` | `node widgets/lively/test/widget.test.mjs` (needs Playwright) |
| Real Music Assistant (opt-in) | `tests/MaSatellite.Integration.Tests` | see below |

On Linux/macOS use `MaSatellite.Linux.slnf` (the Windows-only projects are left out). CI runs everything on `windows-latest` and builds the signed MSIX.

### Integration test against a real Music Assistant

```powershell
$env:MA_HOST  = '192.168.0.98'
$env:MA_TOKEN = '<long-lived token from Music Assistant>'
# optional: $env:MA_TRACK_URI = 'library://track/1'; $env:MA_WEB_PORT = 8095; $env:MA_SENDSPIN_PORT = 8927
dotnet test tests/MaSatellite.Integration.Tests
```

It connects a fake-audio player to the server, finds it in MA, plays a track on it and asserts: stream starts, group is playing, metadata arrives, audio is produced, the player reports in sync, pause stops the group. Without `MA_HOST`/`MA_TOKEN` the test is skipped.

## Manual checklists (one per milestone)

### M0 – skeleton
- [ ] CI is green and publishes the `ma-satellite-msix` artifact.
- [ ] Install via `Install-Dev.ps1` (elevated once); the icon appears in the notification area.
- [ ] Right-click: Open, Library…, Settings…, Start with Windows (checked), Exit. Exit really ends the process.
- [ ] Start the app twice: the second instance exits and the first keeps running.
- [ ] Sign out and in again: the app starts by itself. Untick *Start with Windows*, sign out and in: it does not.
- [ ] `%LOCALAPPDATA%\MaSatellite\logs` (or the package-redirected folder) contains a log; `settings.json` persists changes across restarts.
- [ ] Uninstall via Settings → Apps leaves no tray icon and no startup entry.

### M1 – player core
- [ ] After starting, *ma-satellite* shows up under Music Assistant → Settings → Players within ~10 s (mDNS). If not: add this PC's IP under the Sendspin provider's *Manual IP addresses*, or switch to *Connect* mode in Settings.
- [ ] Play something to the PC: audio starts, no dropouts for 1 hour (check the log for `Dropped an undecodable audio chunk` and the settings window diagnostics: `in sync`, sync error under a few ms).
- [ ] Group the PC with another Sendspin/Cast/other player and clap or play a click track: no audible offset. If there is one, adjust *Static delay* (also adjustable from MA's player settings).
- [ ] Volume: move the MA slider → the Windows volume changes; change the Windows volume (keyboard, flyout) → MA's slider follows within a second. Mute likewise.
- [ ] Switch the output device in Settings or the flyout while playing: audio continues on the new device after a moment. Unplug USB/Bluetooth headphones while playing: playback falls back to / waits for the default device and resumes.
- [ ] Pause/skip in MA behave instantly; seeking in MA does not glitch.

### M2 – mini-player and Windows integration
- [ ] Left-click the tray icon: flyout shows artwork, title/artist/album, seek bar, volume and output device; it closes when you click elsewhere.
- [ ] Play/pause/next/previous and the seek bar control MA; buttons grey out when MA does not offer them.
- [ ] Media keys (play/pause/next/previous) control MA.
- [ ] The Windows volume flyout and the lock screen show the track card with the right info and artwork.

### M3 – local API and widget
- [ ] `curl http://127.0.0.1:8929/api/now-playing` returns the current track; `curl -I http://127.0.0.1:8929/api/artwork` returns an image while playing.
- [ ] The API is not reachable from another PC (`http://<pc-ip>:8929` fails) and a normal website cannot read it (CORS/Host checks).
- [ ] Add `widgets/lively/index.html` in Lively: the card updates on track change and progress moves, without polling (watch the log: no repeated requests).
- [ ] Quit ma-satellite: the widget falls back to Lively's own media info and reconnects by itself when the app starts again.

### M4 – library browsing
- [ ] Settings: enter the MA host and a long-lived token (Music Assistant → your profile → *Long-lived access tokens*), press *Test connection*.
- [ ] Library…: Albums/Artists/Playlists/Tracks load; search finds items across providers; opening an album lists its tracks.
- [ ] Play an album on *This PC* and on another player; *Play next* and *Add to queue* work.
- [ ] Queue tab shows the queue of the chosen player and updates live; *Transfer* moves it to another player and playback continues there.
- [ ] A wrong token shows a clear message with a button to open the settings; the token is never visible in logs.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Player does not appear in MA | Windows must treat the network as *Private* (the firewall rule covers the private profile only), or use *Connect* mode with the MA host in Settings. Add the PC's IP in MA's Sendspin *Manual IP addresses* if mDNS is blocked. |
| "Cannot listen on port 8928" in the settings status | Another program uses the port; change *Listen port* in Settings. |
| Player connects but MA says it uses an unsupported protocol | MA removed *Allow legacy clients*; this build speaks the plaintext Sendspin protocol only (see `docs/NOTES.md`). |
| Audio slightly out of sync with other players | Adjust *Static delay*; Bluetooth outputs often need 100–250 ms. |
| Library says it needs a token | Create a long-lived token in MA and paste it in Settings. |
