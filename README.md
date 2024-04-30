# Found Footage

**MyceliumNetworking library is required.**

Mod that adds real lost footage from other players to your Old World. Anyone can contribute their own recordings, no curated lists or agreements.

This mod must be installed for **all players** in a team.  

## ⚠️ IMPORTANT INFORMATION ⚠️

This mod sends your in-game camera footage to the server __along with audio__, depending on the mod's configuration. **Anyone can watch your videos later.**  
You can request your data or have it removed, see configuration file for more information.

### A note to modpack makers

Do not include `BepInEx/persistent-config` directory in your modpack. People with the same User ID cannot vote twice on the same video.

## Gameplay

You can find lost cameras in the Old World. These contain footage of other players who were there before you.  
You put them into an extraction machine like a normal camera.  
Note that these recordings **will not give you any views or money**.  
When your team dies or comes back alive (configurable), the camera footage is uploaded to the server and made available to other players.

### Voting

Since v0.3.0 you can like or dislike other people's videos. Video's score will be used for pruning system to remove bad videos (as of 22.04.2024, there are already 52k videos that occupy 260 GB).  
If the video is too short **and not funny**, or if it is not in-game footage, go ahead and click Dislike.  
Vote wisely, **you will not be able to change your vote** even in a different lobby.

### Todo
- Spawn cameras at the location where they were lost.
- Store `ContentBuffer` so that videos can give you views.
- Website with statistics?
- Writing comments on videos?

## Rate limiting

**Do not spam API requests.** Do not run HTTP scanners. Doing so will result in your IP address being immediately and **permanently banned**.  
If you want to collaborate, write me on Discord.

## Known bugs

The mod is experimental, bugs may occur, please report them to a [GitHub repository](https://github.com/Assasans/found-footage/issues) if you can.
When submitting a bug report, **please attach your BepInEx log (located at `BepInEx/LogOutput.log`)**.

- "Incompatible mod version! [...] remote: 0.0.0 (incompatible)"
  * Check the mod's config file and remove `/version` suffix from `ServerUrl` property.
  * Try opening the URL in a browser.
- "Failed to get video path"
  * Should be fixed in v0.3.1, please submit a bug report if not.
- Playback stops randomly and no buttons appear
  * Should be fixed in v0.2.2, please submit a bug report if not.
- "Failed to extract" with found cameras
  * Try to change server URL to `https://foundfootage-server.assasans.dev`
  * Most likely one of your team members does not have a mod installed. The game does the extraction on each client separately, and clients without the mod have no found recording data.
  * If you are sure that everyone has the mod installed and working, please submit a bug report.
