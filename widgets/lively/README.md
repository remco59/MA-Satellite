# Lively widget

A now-playing card for [Lively Wallpaper](https://rocksdanister.github.io/lively/) that shows what Music Assistant is playing on this PC.

It talks to the **ma-satellite local API** (`ws://127.0.0.1:8929/api/events`), which pushes every change, so there is no polling. If ma-satellite is not running, it falls back to Lively's own media display (`livelyCurrentTrack`), which ma-satellite also feeds through the Windows media controls.

## Install

1. In Lively choose **Add wallpaper** → **Open**, and select `widgets/lively/index.html` (or drag the folder onto Lively).
2. Choose **Display as widget** if you want it on top of your current wallpaper.
3. Customise via the Lively sliders: API port (default `8929`, change it here if you changed it in ma-satellite), accent colour, opacity, album and progress visibility, and the fluid background on/off.

## Testing without Lively

Open `index.html?port=8929` in a browser while ma-satellite is running. `host` and `port` can be overridden in the URL.

## API used

| Endpoint | Purpose |
|---|---|
| `WS /api/events` | `state` on every change, `progress` once a second while playing |
| `GET /api/artwork?v=N` | cover image (the URL comes in the `state` event) |
| `GET /api/now-playing` | the same JSON as the `state` event, for one-off reads |
| `POST /api/command` | `{"command": "play" \| "pause" \| "next" \| "previous" \| "shuffle" \| "unshuffle" \| "favorite" \| "unfavorite"}` from the hover controls |

The API only listens on `127.0.0.1` and refuses web pages from other origins.

## Fluid background

Behind the card, soft colour blobs drift slowly (inspired by the iOS now-playing look) and fade to the colours of the current cover.

The cover is only used to pick a colour *family*. The widget reads the dominant hue (plus a contrasting accent hue, if the cover has one) and snaps it to a fixed, hand-picked table of 12 palettes in `widget.js` (`PALETTES`). Covers that are grey, black or white use a neutral slate. A muddy or clashing cover therefore can never produce an ugly background: for example an olive cover becomes amber. To change the look, edit that table.

Turn it off in the Lively sliders (**Fluid background**) to get the transparent page back.

## Hover controls

Hover the card and it grows: an info line (year, track number, player) and the controls slide out: shuffle, previous, play/pause, next and a heart for favourites. Shuffle lights up when it is on, and the heart fills when the track is a favourite. Buttons the player does not support are greyed out, and a failed command is explained on the status line.

- Lively only passes the mouse to a wallpaper when its input setting allows it. If hovering does nothing, check Lively's wallpaper input setting.
- Favourites go through Music Assistant: the playing track has to be in this PC's queue. The heart is disabled until that is known, and it is hidden on ma-satellite builds that predate the `favorite` field.
