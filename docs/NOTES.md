# Implementation notes

Decisions made while implementing [PLAN.md](PLAN.md) where the plan was silent or reality differed. Newest first.

## Protocol versions (important)

The plan assumes plain `client/hello` JSON over WebSocket. The Sendspin spec on `main` (as of 2026-10) has since moved to a Noise (`KKpsk2`) encrypted handshake, pairing and a changed hello flow. Music Assistant 2.10.8 supports both; its Sendspin provider setting **Allow legacy clients** (on by default, documented as temporary) still accepts unencrypted legacy clients. ma-satellite implements both:

- **Legacy (plaintext)**: the spec as of commit `8bfa0c5`, i.e. just before "Add encryption support". `SendspinSession`.
- **Encrypted**: `client/init` / `server/init`, then `Noise_KKpsk2_25519_ChaChaPoly_SHA256` (we are the *responder*, the server the initiator), with every later message a WebSocket binary Noise transport message. `SecureTransport` (handshake, PSK selection, fragmentation, in-band re-handshake) and `SecureSendspinSession` (hello/activate flow, availability after clock sync, pairing). The handshake is verified bit-exactly against the official test vectors for both cipher suites (`tests/.../Fixtures/noise-kkpsk2.json`); the pairing token against the spec's reference vector. The crypto is BouncyCastle (X25519, ChaCha20-Poly1305) plus the platform's AES-GCM/SHA-256.
- **Selection** (setting *Sendspin protocol*): *Automatic* (default) tries the encrypted handshake first; a server that does not answer it within 8 s (or answers with an error) is remembered as legacy for 30 minutes and spoken to in the legacy protocol from its next connection (Music Assistant reconnects by itself; in *Connect* mode we reconnect at once). A server that requires the encrypted protocol while we were speaking legacy switches us back. *Encrypted only* and *Legacy only* never fall back.
- **Identity.** The encrypted protocol identifies a player by its static public key (`client_id` = base64url of it), not by the GUID in the settings. So the same PC shows up as a *new* player in Music Assistant the first time the encrypted protocol is used; remove the old entry there.
- **Keys.** Static key, pairing PSK and the long-term PSKs of paired servers are stored as one DPAPI-protected secret (`secrets/sendspin-keys.bin`, `SecretKeyStore`). Corrupt or missing → a fresh identity.
- **Pairing.** Settings shows the *pairing token* (`SP:0…`, client key + pairing PSK); give it to Music Assistant to pair. A pairing server then runs the `pairing_psk` flow: we send `client/pair-init` and `client/pair-finalize` with a random long-term PSK, store it only after `server/pair-finalize`, and the server re-handshakes to it in-band. *Create a new token* revokes a token that leaked; *Forget selected server* drops a long-term PSK.
- **Unpaired access.** Setting *Allow servers that are not paired…* (default on) lets an operator-approved but unpaired server play (Sentinel PSK: encrypted but not authenticated, so a man-in-the-middle on the LAN is possible). When off we refuse such activations with `pairing_required`. `ActivationValidator` implements the spec's admissibility table (`unauthorized`, `pairing_required`, `method_not_supported`).
- **Arbitration.** A pairing attempt is never displaced (a competing server gets `concurrent_attempt`); otherwise the newer connection of equal or higher rank wins.
- **Not verified against a real server.** Everything is tested against in-process fakes that implement the spec independently of our code (`FakeSecureServer`), plus the official vectors. Please try it against your Music Assistant (see `docs/TESTING.md`, "Encrypted protocol"); *Legacy only* is the safe fallback while *Allow legacy clients* is on.

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
- **Secrets.** The MA token and the Sendspin keys are encrypted with DPAPI (current user) in `secrets/` and never logged.
- **Firewall.** The MSIX declares an inbound rule for TCP 8928 on *private* networks only; on public networks use *Connect* mode.

## Known gaps / next steps

- Encrypted protocol: only the `pairing_psk` pairing method is offered (no QR/dynamic pairing code UI), output/visualizer-less roles only (player, controller, metadata, artwork); first runs against a real Music Assistant may need tuning.
- Opus decoder, visualizer role, per-device multiple instances (all listed under "Later" in the plan).
- Auto-update and a real code-signing certificate (plan: "Later").
