# Advanced Music Assistant features in MA-Satellite

MA-Satellite keeps the Music Assistant server as the source of truth. This window exposes selected server capabilities through the public WebSocket API; it does not duplicate the server's player/provider logic.

## Multi-room grouping

Choose an available player as the group leader, select one or more available players, and use **Group selected**. Music Assistant checks whether the player/provider combination can be synchronized. Use **Ungroup player** to remove an individual player from its current sync/group.

Grouping is not guaranteed for every combination of player providers. The server decides whether a requested group is valid.

## Intelligent playback

Select a player, then load its queue state.

- **Autoplay** continues the queue when it is nearing its end.
- **Autoplay mode** chooses automatic selection, similar tracks, similar artists, a library mix, or a selected playlist. Similarity modes depend on providers that expose the corresponding feature.
- **Smart Shuffle** is a per-queue setting. It only becomes active while Shuffle is enabled. The existing player bar remains the place to turn Shuffle on/off.
- **Crossfade** can be enabled per queue, with Smart Crossfade or Standard Crossfade selected as the server-side mode.

Music Assistant enforces its own constraints; for example, it can reject enabling Autoplay while Repeat is active. A failed operation is shown in the window status text.

## Sleep timer

Select a player in the Intelligent playback section and choose:
- A duration (1–1440 minutes).
- End of the current item.
- End of the current chapter.
- Clear timer.

End-of-item and end-of-chapter timers require a supported active queue item. Chapter timers also require chapter metadata from the media provider.

## Podcasts and audiobooks

The library selector can load Audiobooks, Podcasts, Genres and In progress items. Choose a podcast and use **Load podcast episodes** to list its episodes. Select any playable item and choose **Play selected** to start it on the selected player.

For the current audiobook or podcast episode, the playback speed control supports 0.5× to 3.0×. Music Assistant rejects this command when the selected queue is not playing an audiobook or podcast episode.

In-progress and resume metadata are returned by the server. Resume behaviour and availability depend on the media provider.

## Provider-aware search

Search includes tracks, artists, albums, playlists, radio, podcasts, podcast episodes, audiobooks and genres. The optional provider field accepts comma-separated Music Assistant provider instance IDs. Leave it blank to search every provider included by Music Assistant.

## Playlist management

The window supports creating a library playlist, adding a selected track from search, removing selected playlist entries, importing M3U/M3U8 text and exporting a playlist as M3U8 text.

Import/export uses the server's playlist endpoints. The server determines which external playlist formats and provider-backed playlists are editable.

## Server-side plugins

AI Radio, Party Mode, Music Quiz, visualizers and other plugins remain server-side features. Install and configure those in Music Assistant. The **Open Music Assistant** shortcut opens the server's web interface; MA-Satellite does not install plugins or duplicate their screens.

## Intentionally excluded

The Audio Control Centre is out of scope: MA-Satellite does not add DSP/equalizer controls, DSP presets or audio pipeline administration in this feature set.
