# Changelog

## v0.3.1

Note: Spawning cameras at lost position and gaining views is still not implemented due to how the mod spawns fake cameras.

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
