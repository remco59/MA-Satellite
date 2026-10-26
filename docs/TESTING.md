# Testing

## Automated

| Layer | Where | Run |
|---|---|---|
| Protocol parsing, clock-sync math, buffer/drift logic, decoders (FLAC bit-exact against ffmpeg fixtures), volume mapping | `tests/MaSatellite.Core.Tests`, `tests/MaSatellite.Windows.Tests` | `dotnet test MaSatellite.Tests.slnf` |
| End to end: the real player against an in-process fake Sendspin server over real sockets (handshake, timing within a few ms, pitch, volume both ways, artwork, arbitration, reconnect) | `tests/MaSatellite.Core.Tests/SendspinServiceTests.cs` | same |
| Encrypted protocol: Noise vectors, pairing token, secure transport (PSK selection, fragmentation, rekey, tampering), the whole session incl. pairing/unpair/arbitration/legacy fallback against an independent fake secure server | `tests/MaSatellite.Core.Tests/{Noise,PairingToken,SecureTransport,SecureSendspinService,ActivationValidator}Tests.cs` | same |
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

### `.exe` installer
- [ ] CI is green and publishes the `ma-satellite-installer` artifact (the *Test installer* step already does a silent install/start/uninstall).
- [ ] Run `ma-satellite-setup-<version>.exe` by double-click (SmartScreen: *More info → Run anyway*). It installs without admin rights; tray icon appears after *Start ma-satellite* at the end.
- [ ] Run it again with *Install for all users* ticked and the firewall task selected: `Get-NetFirewallRule -DisplayName ma-satellite` shows the inbound TCP 8928 rule (private profile).
- [ ] Task Manager → Startup apps lists ma-satellite. Disable it there: the tray menu shows *Start with Windows* unchecked and greyed; sign out/in: no autostart. Enable it again there: autostart works.
- [ ] Install a newer build over the old one while the app runs: it is stopped, replaced and the settings survive.
- [ ] Uninstall via Settings → Apps: no tray icon, no startup entry, no firewall rule; the prompt about settings/logs does what you answer.

### M1 – player core
- [ ] After starting, *ma-satellite* shows up under Music Assistant → Settings → Players within ~10 s (mDNS). If not: add this PC's IP under the Sendspin provider's *Manual IP addresses*, or switch to *Connect* mode in Settings.
- [ ] With Music Assistant 2.10.8, start a track and confirm that the log says `Stream started: pcm ...`, no FLAC chunks are dropped, the buffer grows above 0 ms, and audio is audible. PCM 16-bit at 44.1/48 kHz is intentionally the **only advertised audio format** until the real-server FLAC frame-sync error is fixed; re-enable FLAC only after testing real FLAC payloads.
- [ ] Select the actual speaker/headphone endpoint if Windows `System default` resolves to a disconnected digital output (e.g. Realtek Digital Output).
- [ ] Play something to the PC: audio starts, no dropouts for 1 hour (check the log for `Dropped an undecodable audio chunk` and the settings window diagnostics: `in sync`, sync error under a few ms).
- [ ] Group the PC with another Sendspin/Cast/other player and clap or play a click track: no audible offset. If there is one, adjust *Static delay* (also adjustable from MA's player settings).
- [ ] Volume: move the MA slider → the Windows volume changes; change the Windows volume (keyboard, flyout) → MA's slider follows within a second. Mute likewise.
- [ ] Switch the output device in Settings or the flyout while playing: audio continues on the new device after a moment. Unplug USB/Bluetooth headphones while playing: playback falls back to / waits for the default device and resumes.
- [ ] Pause/skip in MA behave instantly; seeking in MA does not glitch.

### Encrypted protocol (once, with *Allow legacy clients* still on)
- [ ] Settings → *Sendspin protocol* = *Encrypted only*, save. The player appears in MA (possibly as a new player); the settings status line says `Encrypted · server not paired` (or `waiting for you to approve`).
- [ ] Play something: audio starts, sync as with legacy (`in sync`, small sync error). Volume/mute both ways work.
- [ ] Copy the pairing token from Settings and pair the PC in MA; afterwards the status line says `Encrypted · paired server`, and *Paired servers* lists it. Restart the app: it still plays without approval.
- [ ] *Forget selected server* and reconnect: MA offers to pair again. *Create a new token*: the old token is rejected by MA.
- [ ] Untick *Allow servers that are not paired…* while unpaired: the connection ends with `pairing_required` in the log.
- [ ] *Automatic* with *Allow legacy clients* on and encryption working: log shows the encrypted session, not legacy.

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
| Player connects but nothing plays / MA reports a protocol problem | Try *Settings → Sendspin protocol*: *Legacy only* works while MA's *Allow legacy clients* is on, *Encrypted only* once it is off. The log says which protocol was used and why a handshake failed. |
| MA shows the PC twice | The encrypted protocol identifies the player by its key, so it appears as a new player the first time; remove the old entry in MA. |
| "approve this player in Music Assistant" | The server is not paired with this PC and has not enabled it yet; enable the player in MA, or pair it (token in Settings). |
| Audio slightly out of sync with other players | Adjust *Static delay*; Bluetooth outputs often need 100–250 ms. |
| Library says it needs a token | Create a long-lived token in MA and paste it in Settings. |
