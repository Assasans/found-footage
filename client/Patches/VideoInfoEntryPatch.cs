using HarmonyLib;

namespace FoundFootage.Patches;

[HarmonyPatch(typeof(VideoInfoEntry))]
internal static class VideoInfoEntryPatch {
  [HarmonyPostfix]
  [HarmonyPatch("GetString")]
  public static void GetString(VideoInfoEntry __instance, ref string __result) {
    if(GuidUtils.IsLocal(__instance.videoID.id)) {
      __result = "Found camera";
    }
  }
}
