using HarmonyLib;
using Photon.Pun;

namespace FoundFootage.Patches;

[HarmonyPatch(typeof(ExtractVideoMachine))]
internal static class ExtractVideoMachinePatch {
  [HarmonyPostfix]
  [HarmonyPatch("RPC_Success")]
  internal static void RPC_Success(ExtractVideoMachine __instance) {
    FoundFootagePlugin.Logger.LogInfo("RPC_Success shitted");
    if(!PhotonNetwork.IsMasterClient) return;

    var chance = FoundFootagePlugin.Instance.PassUploadChance.Value;
    // Too bad
    if(chance.AlmostEquals(0f, 0.01f)) return;

    var recordings = RecordingsHandler.GetRecordings();
    foreach(var (videoID, recording) in recordings) {
      FoundFootagePlugin.Logger.LogInfo($"Check {videoID}");
      if(GuidUtils.IsLocal(videoID.id)) continue;

      if(FoundFootagePlugin.Instance.Random.NextDouble() <= chance) {
        PhotonGameLobbyHandlerPatch.UploadRecording(FoundFootagePlugin.Instance, videoID, recording, "extract", null);
      } else {
        FoundFootagePlugin.Logger.LogInfo("Do not uploading extracted");
      }
    }
  }
}
