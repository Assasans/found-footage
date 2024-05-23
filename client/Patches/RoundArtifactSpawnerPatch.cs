using System;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace FoundFootage.Patches;

[HarmonyPatch(typeof(ArtifactSpawner))]
internal static class RoundArtifactSpawnerPatch {
  [HarmonyPostfix]
  [HarmonyPatch("Start")]
  internal static void Awake(ArtifactSpawner __instance) {
    FoundFootagePlugin.Logger.LogInfo("Start shitted");

    if(!PhotonNetwork.IsMasterClient) {
      FoundFootagePlugin.Logger.LogWarning("Not a master client");
      return;
    }

    if(FoundFootagePlugin.Instance.Random.NextDouble() <= FoundFootagePlugin.Instance.SpawnChance.Value) {
      FoundFootagePlugin.Logger.LogInfo("[RoundArtifactSpawnerPatch] Spawning found camera!");

      Vector3 pos = __instance.transform.position + Vector3.up * __instance.spawnAbovePatrolPoint;
      ItemInstanceData instance = new ItemInstanceData(Guid.NewGuid());
      VideoInfoEntry entry = new VideoInfoEntry();
      entry.videoID = new VideoHandle(GuidUtils.MakeLocal(Guid.NewGuid()));
      entry.maxTime = 1;
      entry.timeLeft = 0;
      entry.SetDirty();
      instance.AddDataEntry(entry);
      FoundFootagePlugin.Instance.FakeVideos.Add(entry.videoID);
      FoundFootagePlugin.Logger.LogInfo($"[RoundArtifactSpawnerPatch] added entry {entry.videoID.id}");
      PickupHandler.CreatePickup(1, instance, pos,
        UnityEngine.Random.rotation);
      FoundFootagePlugin.Logger.LogInfo($"[RoundArtifactSpawnerPatch] Spawned found camera at {pos}!");
    }
  }
}
