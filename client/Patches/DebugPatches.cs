#undef DEBUG

using System;
using HarmonyLib;
using UnityEngine;

namespace FoundFootage.Patches;

#if DEBUG

[HarmonyPatch(typeof(SurfaceNetworkHandler))]
internal static class SurfaceNetworkHandlerPatch {
  [HarmonyPostfix]
  [HarmonyPatch("InitSurface")]
  internal static void SpawnOnStartRun() {
    TimeOfDayHandler.SetTimeOfDay(TimeOfDay.Evening);
    for(int index = 0; index < 20; index++) {
      SpawnExtraCamera();
    }
  }

  private static void SpawnExtraCamera() {
    FoundFootagePlugin.Logger.LogInfo("Spawning extra camera!");

    ItemInstanceData instance = new ItemInstanceData(Guid.NewGuid());
    VideoInfoEntry entry = new VideoInfoEntry();
    entry.videoID = new VideoHandle(GuidUtils.MakeLocal(Guid.NewGuid()));
    entry.maxTime = 1;
    entry.timeLeft = 0;
    entry.SetDirty();
    instance.AddDataEntry(entry);
    FoundFootagePlugin.Instance.FakeVideos.Add(entry.videoID);
    FoundFootagePlugin.Logger.LogInfo($"added entry {entry.videoID.id}");
    Pickup pickup = PickupHandler.CreatePickup(1, instance, new Vector3(-14.842f, 2.418f, 8.776f),
      Quaternion.Euler(0f, -67.18f, 0f));
    FoundFootagePlugin.Logger.LogInfo("Spawned extra camera!");

    if(CameraHandler.TryGetCamera(instance.m_guid, out VideoCamera camera)) {
    } else {
      FoundFootagePlugin.Logger.LogError("No VideoCamera found");
    }
  }
}

// [HarmonyPatch(typeof(VerboseDebug))]
// internal static class VerboseDebugPatch {
//   [HarmonyPostfix]
//   [HarmonyPatch("Log")]
//   internal static void Log(string message) {
//     Debug.Log(message);
//   }
//
//   [HarmonyPostfix]
//   [HarmonyPatch("LogError")]
//   internal static void LogError(string s) {
//     Debug.LogError(s);
//   }
// }

#endif
