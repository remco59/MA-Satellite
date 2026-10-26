# Lively widget

A now-playing card for [Lively Wallpaper](https://rocksdanister.github.io/lively/) that shows what Music Assistant is playing on this PC.

It talks to the **ma-satellite local API** (`ws://127.0.0.1:8929/api/events`), which pushes every change, so there is no polling. If ma-satellite is not running, it falls back to Lively's own media display (`livelyCurrentTrack`), which ma-satellite also feeds through the Windows media controls.

## Install

1. In Lively choose **Add wallpaper** → **Open**, and select `widgets/lively/index.html` (or drag the folder onto Lively).
2. Choose **Display as widget** if you want it on top of your current wallpaper.
3. Customise via the Lively sliders: API port (default `8929`, change it here if you changed it in ma-satellite), accent colour, opacity, album and progress visibility.

## Testing without Lively

Open `index.html?port=8929` in a browser while ma-satellite is running. `host` and `port` can be overridden in the URL.

## API used

| Endpoint | Purpose |
|---|---|
| `WS /api/events` | `state` on every change, `progress` once a second while playing |
| `GET /api/artwork?v=N` | cover image (the URL comes in the `state` event) |
| `GET /api/now-playing` | the same JSON as the `state` event, for one-off reads |

The API only listens on `127.0.0.1` and refuses web pages from other origins.
