using System.Collections.Generic;
using HarmonyLib;

namespace FoundFootage.Patches;

[HarmonyPatch(typeof(ContentBuffer))]
internal static class ContentBufferPatch {
  [HarmonyPostfix]
  [HarmonyPatch("GenerateComments")]
  internal static void GenerateComments(ContentBuffer __instance, ref List<Comment> __result) {
    // Fake video
    if(__instance.buffer.Count == 0) {
      __result.Add(new Comment("bruh you stole this from another team") {
        Likes = 0,
        Time = 0,
        Face = 0,
        FaceColor = 0
      });
    }
  }
}
