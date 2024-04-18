# Found Footage

**MyceliumNetworking library is required.**

Mod that adds real lost footage from other players to your Old World. Anyone can contribute their own recordings, no curated lists or agreements.

This mod must be installed for **all players** in a team for the best experience.  
To **upload footage**, the lobby host player must have the mod installed.  
You must have the mod installed to **interact** with lost cameras and view recordings.

## IMPORTANT INFORMATION

This mod sends your in-game camera footage to the server __along with audio__, depending on the mod's configuration. **Anyone can watch your videos later.**  
You can request your data or have it removed, see configuration file for more information.

## Gameplay

You can find lost cameras in the Old World. These contain footage of other players who were there before you.  
You put them into an extraction machine like a normal camera.  
Note that these recordings **will not give you any views or money**.

### Voting

Since v0.3.0 you can like or dislike other people's videos. Video's score will be used for pruning system to remove bad videos (as of 18.04.2024, there are already 25k videos that occupy 125 GB).  
If the video is too short **and not funny**, or if it is not in-game footage, go ahead and click Dislike.  
Vote wisely, **you will not be able to change your vote** even in a different lobby.

## Known bugs

The mod is experimental, bugs may occur, please report them to a [GitHub repository](https://github.com/Assasans/found-footage/issues) if you can.
When submitting a bug report, **please attach your BepInEx log (located at `BepInEx/LogOutput.log`)**.

- Playback stops randomly and no buttons appear
  * Should be fixed in v0.2.2, please submit a bug report if not.
- "Failed to extract" with found cameras
  * Try to change server URL to `https://foundfootage-server.assasans.dev`
  * Most likely one of your team members does not have a mod installed. The game does the extraction on each client separately, and clients without the mod have no found recording data.
  * If you are sure that everyone has the mod installed and working, please submit a bug report.
