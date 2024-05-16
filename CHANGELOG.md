# Changelog

## v0.4.3

- client: Disable debug code, again ([#1278](https://github.com/Assasans/found-footage/issues/27))

## v0.4.2

**Do not use this version**

- client: Add config tracing
- client: Add found video filters setting
- **client: Migrate persistent config to ".privatecfg"** ([#18](https://github.com/Assasans/found-footage/issues/18))
- client: Serialize all fields

## v0.4.1

- client: Fix always uploading videos

## v0.4.0

**Do not use this version**

Note: Spawning cameras at lost position is still not implemented due to how the mod spawns fake cameras.

- client: Change fake camera HUD title (replaces `0% FILM LEFT` message)
- server: Add v3 get video endpoint
- client: **Content buffer for remote fake videos** (you can now gain views, [#21](https://github.com/Assasans/found-footage/issues/21))
- client: Use UnityWebRequest ([#20](https://github.com/Assasans/found-footage/issues/20))
- client: Allow to completely disable uploading ([#23](https://github.com/Assasans/found-footage/issues/23))

## v0.3.1

- client: Send player count
- client: Persistent config and send more info
- client: Do not upload duplicate videos
- client: **Send position and content buffer on upload**
- client: Add Mycelium as BepInEx dependency (fixes logger initialization exception)
- client: Call SetValid on fake clips (**fixes incompatibility with MoreCameras**)
- client: Add version to upload video request
- client: Add ContentWarningPlugin attribute (not vanilla compatible)
- client: Make default pass upload chance 25%
- server: **Implement rate limiting**

## v0.3.0

- **Voting system**
- client: Send lobby ID along with video
- client: Try to update server URL on error

## v0.2.2

- client: Share video URL instead of contents (fixed video freezing)
- client: Change default server URL
- client: Improve logging
- client: Make message box modal

## v0.2.1

- client: Prevent found cameras from affecting objective
- client: Fix 100% film left
- client: Remove unused code

## v0.2.0

Initial release
