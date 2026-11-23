# Music Assistant controls in MA-Satellite

MA-Satellite keeps the Music Assistant server as the source of truth. Its native controls are surfaced in the part of the interface where they are used, not in a separate feature/developer window.

## Multi-room grouping

Open the **playback target** selector in the top bar. Choose **Group speakers…**, select an available group leader, then select the players to add. Use **Ungroup current player** from the same selector to remove the selected player from its current group.

Grouping is not guaranteed for every combination of player providers. Music Assistant decides whether a requested group is valid.

## Queue settings

Open **Now Playing → Up Next → Queue settings** for the selected player.

- **Autoplay** continues the queue when it is nearing its end.
- **Autoplay mode** chooses automatic selection, similar tracks, similar artists, a library mix, or a selected playlist. Similarity modes depend on providers that expose the corresponding feature.
- **Smart Shuffle** is a per-queue setting. It only becomes active while Shuffle is enabled; use the regular Shuffle control in the player bar to turn Shuffle on or off.
- **Crossfade** can be enabled per queue, with Smart Crossfade or Standard Crossfade selected as the server-side mode.

Music Assistant enforces its own constraints; for example, it can reject enabling Autoplay while Repeat is active. Errors are surfaced in the main window.

## Sleep timer

Open **Now Playing → Up Next → Sleep timer**. Presets are available alongside custom durations, end-of-item, end-of-chapter and clear timer actions. A custom duration can be between 1 and 1440 minutes.

End-of-item and end-of-chapter timers require a supported active queue item. Chapter timers also require chapter metadata from the media provider.

## Podcasts, audiobooks and genres

Use the **Audiobooks**, **Podcasts**, **Genres** and **Continue listening** destinations in the library sidebar. Open a podcast show to list its episodes; open an audiobook or genre where the provider exposes child items. Playable podcast episodes can be played directly.

For a currently playing audiobook or podcast episode, use the speed control on the Now Playing page to request playback speed from 0.5× to 3.0×. Music Assistant may reject variable speed for other media types.

In-progress and resume metadata come from the server. Resume behaviour and availability depend on the media provider.

## Provider-aware search

Search includes tracks, artists, albums, playlists, radio, podcasts, podcast episodes, audiobooks and genres. Select **Providers** in the search filter row and enter comma-separated Music Assistant provider instance IDs. Leave the provider field empty to search every provider included by Music Assistant.

## Playlist management

On the **Playlists** page, choose **New playlist** or **Import M3U / M3U8**. A playlist item's context menu exports M3U8 text to the clipboard. A track's context menu can add it to any playlist; when viewing a playlist, its track menu also offers **Remove from this playlist**.

Import/export and editing use the server's playlist endpoints. The server determines which external playlist formats and provider-backed playlists are editable.

## Server-side plugins

AI Radio, Party Mode, Music Quiz, visualizers and other plugins remain server-side features. Install and configure those in Music Assistant. MA-Satellite does not install plugins or duplicate their screens.

## Intentionally excluded

The Audio Control Centre is out of scope: MA-Satellite does not add DSP/equalizer controls, DSP presets or audio pipeline administration in this feature set.
