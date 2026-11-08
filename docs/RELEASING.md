# Releasing

Maintainer notes for publishing a release and checking the desktop UI by hand.

## Desktop UI acceptance test (Music Assistant connected)

The Windows CI builds and smoke-tests the installer, but it cannot see your personal Music Assistant server, speakers, or screen. After installing the v0.2.0 release, verify on your own PC:

1. Open the main window and resize it to about 1180×790, then below 900px wide. The sidebar, album cards and player should remain readable and the main navigation should work.
2. Open Home, Library (Albums/Artists/Tracks), Playlists, Search and Queue. Confirm that album/track cover art and empty/loading states display correctly.
3. Select **This PC**, start playback from Music Assistant and compare the mini-player with the main footer. Title, artwork, play/pause and progress should agree.
4. Play an album from Home or Library; check seek, previous/next, volume/mute and queue. For a remote Music Assistant player, also check shuffle and repeat when supported.
5. Unplug Music Assistant or enter an invalid server to inspect the error/retry state; reconnect and verify the library refreshes.

Use a screenshot of the main window and mini-player plus the log in `%LOCALAPPDATA%\MaSatellite\logs` when filing a visual or behavior bug.

## Publishing a release

Set `<Version>` in `Directory.Build.props` (and the manifest's `Identity Version`), merge, then push a matching tag (for example `git tag v0.2.0 && git push origin v0.2.0`). CI builds, tests and attaches the installer and MSIX files to a new GitHub Release (it fails if the tag and the version disagree).
