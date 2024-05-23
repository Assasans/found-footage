using HarmonyLib;

namespace FoundFootage.Patches;

[HarmonyPatch(typeof(UploadCompleteUI))]
internal static class UploadCompleteUIPatch {
  [HarmonyPostfix]
  [HarmonyPatch("PlayVideo")]
  public static void PlayVideo(UploadCompleteUI __instance, IPlayableVideo playableVideo) {
    if(!FoundFootagePlugin.Instance.VotingEnabled.Value) {
      FoundFootagePlugin.Logger.LogInfo("Voting is disabled, not patching UploadCompleteUI.PlayVideo");
      return;
    }

    if(playableVideo is CameraRecording recording) {
      FoundFootagePlugin.Logger.LogInfo("UploadCompleteUI.PlayVideo is CameraRecording");

      bool isLocal = GuidUtils.IsLocal(recording.videoHandle.id);
      UploadVideoStationPatch.LikeUi.SetActive(isLocal);
      UploadVideoStationPatch.DislikeUi.SetActive(isLocal);
      UploadVideoStationPatch.LikeInteractable.SetActive(isLocal);
      UploadVideoStationPatch.DislikeInteractable.SetActive(isLocal);

      if(isLocal) {
        var root = __instance.gameObject.transform.parent.parent.parent;
        var interactableRoot = root.Find("SaveVideoToDesktopInteractable").gameObject;
        interactableRoot.GetComponentInChildren<LikeInteractable>().SetRecording(recording);
        interactableRoot.GetComponentInChildren<DislikeInteractable>().SetRecording(recording);
        FoundFootagePlugin.Logger.LogInfo("UploadCompleteUI.PlayVideo completed");
      }
    } else {
      FoundFootagePlugin.Logger.LogWarning("UploadCompleteUI.PlayVideo not CameraRecording");
    }
  }

  // What the fuck
  // [HarmonyTranspiler]
  // [HarmonyPatch("DisplayVideoEval", MethodType.Enumerator)]
  // public static IEnumerable<CodeInstruction> DisplayVideoEval() {
  //   var enumeratorType = typeof(UploadCompleteUI).Assembly.GetTypes()
  //     .Single(type => type.Name.Contains("<DisplayVideoEval>"));
  //
  //   return new CodeMatcher()
  //     .MatchForward(false,
  //       new CodeMatch(OpCodes.Callvirt,
  //         AccessTools.DeclaredMethod(typeof(SaveVideoToDesktopInteractable),
  //           nameof(SaveVideoToDesktopInteractable.SetRecording))))
  //     .InsertAndAdvance(
  //       new CodeInstruction(OpCodes.Nop)
  //       // new CodeInstruction(OpCodes.Ldarg_0),
  //       // new CodeInstruction(OpCodes.Pop)
  //       // new CodeInstruction(OpCodes.Callvirt, AccessTools.DeclaredPropertyGetter(typeof(Component), nameof(Component.gameObject))),
  //       // new CodeInstruction(OpCodes.Callvirt, AccessTools.DeclaredPropertyGetter(typeof(GameObject), nameof(GameObject.transform))),
  //       // new CodeInstruction(OpCodes.Callvirt, AccessTools.DeclaredPropertyGetter(typeof(Transform), nameof(Transform.parent))),
  //       // new CodeInstruction(OpCodes.Callvirt, AccessTools.DeclaredPropertyGetter(typeof(Transform), nameof(Transform.parent))),
  //       // new CodeInstruction(OpCodes.Callvirt, AccessTools.DeclaredPropertyGetter(typeof(Transform), nameof(Transform.parent))),
  //       // new CodeInstruction(OpCodes.Ldstr, "SaveVideoToDesktopInteractable"),
  //       // new CodeInstruction(OpCodes.Callvirt, AccessTools.DeclaredMethod(typeof(Transform), nameof(Transform.Find))),
  //       // new CodeInstruction(OpCodes.Callvirt, AccessTools.DeclaredPropertyGetter(typeof(Component), nameof(Component.gameObject))),
  //       // new CodeInstruction(OpCodes.Callvirt, AccessTools.GetDeclaredMethods(typeof(GameObject)).Single(method => method.Name == nameof(GameObject.GetComponentInChildren) && method.ContainsGenericParameters && method.GetParameters().Length == 0).MakeGenericMethod(typeof(LikeInteractable))),
  //       // new CodeInstruction(OpCodes.Ldarg_0),
  //       // // IL_029a: ldfld        class CameraRecording UploadCompleteUI/'<DisplayVideoEval>d__15'::recording
  //       // new CodeInstruction(OpCodes.Ldfld, AccessTools.DeclaredField(enumeratorType, "recording")),
  //       // new CodeInstruction(OpCodes.Callvirt, AccessTools.DeclaredMethod(typeof(LikeInteractable), nameof(LikeInteractable.SetRecording)))
  //     )
  //     .InstructionEnumeration();
  //   // Escape McScreen/Content/ShowVideoState
  // }
}
