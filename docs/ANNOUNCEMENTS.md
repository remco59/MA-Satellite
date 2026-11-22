# Native announcement ducking

MA-Satellite can mix an announcement over its current Sendspin music stream. The music is attenuated while the announcement is playing and is restored with a short fade afterwards. Announcements are decoded off the WASAPI render thread; the render callback only applies the gain envelope and adds already-decoded PCM.

## Components

- MA-Satellite's AnnouncementMixer and Windows Media Foundation decoder.
- An authenticated LAN endpoint on TCP 8930 (configurable) for announcement PCM.
- A Home Assistant ma_satellite media-player integration that advertises MEDIA_ANNOUNCE.
- A small Music Assistant server provider change so Sendspin can discover entities registered by the ma_satellite integration domain.

The LAN endpoint accepts private-network clients only and requires the MA-Satellite pairing token as a bearer token. It accepts at most 30 MB per item, with a two-minute per-item decode limit and a three-minute queued-audio limit.

## Setup

1. In MA-Satellite, open **Settings → Advanced · Network & diagnostics** and enable **Home Assistant announcements over the LAN**. Leave the port at **8930** unless it conflicts with another service.
2. Allow inbound TCP 8930 on the Windows **Private** network profile. Do not expose this port to the public internet.
3. Copy the `custom_components/ma_satellite` folder into the Home Assistant `custom_components` directory, restart Home Assistant, then add **MA Satellite** from **Settings → Devices & services → Add integration**.
4. Enter the Windows PC hostname/IP, port 8930, and the pairing token from **MA-Satellite → Settings → Security & pairing**.
5. Apply the small discovery patch to the Music Assistant server source. From a checkout of [music-assistant/server](https://github.com/music-assistant/server), run:

   ```bash
   git apply /path/to/MA-Satellite/patches/music-assistant-server/ma-satellite-announcement-discovery.patch
   ```

   Then rebuild/redeploy your Music Assistant server from that patched source. Do not edit a running container's files directly; those changes will be lost on restart.
6. In Music Assistant, reload the Sendspin provider or restart the patched server. The Windows Sendspin player should expose announcement support once the MA-Satellite entity appears in Home Assistant.

The server-side patch is required until equivalent discovery is included in the Music Assistant release you run. Without it, Music Assistant's Sendspin provider only searches the ESPHome integration domain and will not find the custom MA-Satellite entity.

## Behaviour

Home Assistant fetches the media item (including media-source items) and forwards its encoded bytes to MA-Satellite. MA-Satellite decodes it to the current WASAPI output format, queues it, mixes it over the Sendspin music and ducks the music to approximately 24% while the clip plays. Multiple requests are queued in arrival order.

The first implementation supports announcement mixing while a Sendspin stream is active. It does not start a standalone audio stream when no Sendspin player stream/output is active. The announcement endpoint is separate from the loopback-only desktop widget API.

## Troubleshooting

- **The HA config flow cannot connect:** verify host, TCP 8930, Windows firewall and token.
- **The player is visible but Music Assistant does not show announcements:** apply the Music Assistant server patch and reload the Sendspin provider.
- **The announcement is not audible:** make sure This PC is actively receiving a Sendspin stream and that its selected WASAPI output is available.
- **The API rejects a request:** check the Windows app log for the AnnouncementApiServer or WindowsAnnouncementPlayer entry.
