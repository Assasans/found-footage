using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Photon.Pun;
using Unity.Collections;
using UnityEngine;
using Zorro.Core.Serizalization;
using Object = UnityEngine.Object;

namespace FoundFootage.Patches;

[HarmonyPatch(typeof(PhotonGameLobbyHandler))]
internal static class PhotonGameLobbyHandlerPatch {
  public static IEnumerator WaitThen(float t, Action a) {
    yield return new WaitForSecondsRealtime(t);
    a();
  }

  [HarmonyPostfix]
  [HarmonyPatch("ReturnToSurface")]
  internal static void ReturnToSurface(PhotonGameLobbyHandler __instance) {
    FoundFootagePlugin.Logger.LogInfo("ReturnToSurface enter");
    if(Object.FindObjectsOfType<Player>().Where(pl => !pl.ai).All(player => player.data.dead)) {
      FoundFootagePlugin.Logger.LogInfo("CheckForAllDead true");

      var chance = FoundFootagePlugin.Instance.DeathUploadChance.Value;
      // Too bad
      if(chance.AlmostEquals(0f, 0.01f)) return;

      __instance.StartCoroutine(WaitThen(5f, () => {
        var recordings = RecordingsHandler.GetRecordings();
        var cameras = (Dictionary<Guid, VideoCamera>)AccessTools.DeclaredField(typeof(CameraHandler), "m_cameras")
          .GetValue(CameraHandler.Instance);
        foreach(var (videoID, recording) in recordings) {
          FoundFootagePlugin.Logger.LogInfo($"Check {videoID}");
          if(GuidUtils.IsLocal(videoID.id)) continue;

          if(FoundFootagePlugin.Instance.Random.NextDouble() <= chance) {
            var camera = cameras.Values.SingleOrDefault(camera => {
              var entry = (VideoInfoEntry)AccessTools.DeclaredField(typeof(VideoCamera), "m_recorderInfoEntry")
                .GetValue(camera);
              return entry.videoID.id == videoID.id;
            });

            // Unity is cursed, cannot use `?.` because lifetime checks...
            var position = camera != null
              ? camera.transform != null ? camera.transform.position : (Vector3?)null
              : null;
            UploadRecording(FoundFootagePlugin.Instance, videoID, recording, "death", position);
          } else {
            FoundFootagePlugin.Logger.LogInfo("Do not extracting");
          }
        }

        FoundFootagePlugin.Logger.LogInfo("CheckForAllDead shitted");
      }));
    }
  }

  public static void UploadRecording(MonoBehaviour target, VideoHandle videoID, CameraRecording recording,
    string reason, Vector3? position) {
    // I have no idea why it is being called multiple times, both for deaths and extracts.
    if(FoundFootagePlugin.Instance.SentVideos.Contains(videoID)) {
      FoundFootagePlugin.Logger.LogWarning($"Not uploading duplicate video {videoID}");
      return;
    }

    FoundFootagePlugin.Instance.SentVideos.Add(videoID);

    // Must be called from a main thread
    var contentBuffer = FoundFootagePlugin.Instance.SendContentBufferEnabled.Value
      ? SerializeContentBuffer(recording)
      : null;
    new Thread(() => {
      try {
        bool success = false;
        for(int attempt = 0; attempt < 30; attempt++) {
          success |= RecordingsHandler.Instance.ExtractRecording(recording);
          if(success) break;

          FoundFootagePlugin.Logger.LogWarning($"Failed to extract {videoID}, retrying...");
          Thread.Sleep(5000);
        }

        if(success) {
          var path = Path.Combine(recording.GetDirectory(), "fullRecording.webm");
          FoundFootagePlugin.Logger.LogInfo($"Extracted {videoID}: {path}");

          for(int attempt = 0; attempt < 10; attempt++) {
            try {
              FoundFootagePlugin.Logger.LogInfo($"Content buffer: {contentBuffer?.Length ?? 0} bytes");
              HttpUtils.UploadVideo(
                target,
                path,
                new Dictionary<string, string> {
                  ["video_id"] = recording.videoHandle.id.ToString(),
                  ["user_id"] = FoundFootagePlugin.Instance.UserId.Value,
                  ["secret_user_id"] = FoundFootagePlugin.Instance.SecretUserId.Value,
                  ["lobby_id"] = PhotonNetwork.CurrentRoom.Name,
                  ["language"] = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName,
                  ["position"] = position != null ? $"{position.Value.x}@{position.Value.y}@{position.Value.z}" : "",
                  ["version"] = PluginInfo.PLUGIN_VERSION,
                  ["day"] = SurfaceNetworkHandler.RoomStats.CurrentDay.ToString(),
                  ["player_count"] = Object.FindObjectsOfType<Player>().Count(pl => !pl.ai).ToString(),
                  ["reason"] = reason,
                  // multipart/form-data is cursed, and FCL is even more cursed, so the easiest way is to just Base64 encode...
                  ["content_buffer"] = contentBuffer != null ? Convert.ToBase64String(contentBuffer) : ""
                }
              ).GetAwaiter().GetResult();
              FoundFootagePlugin.Logger.LogInfo("Uploaded video!");

              break;
            } catch(IOException exception) {
              // Issue #14
              // I have no idea what could be holding a write lock on a file, so just keep trying until it succeeds...
              FoundFootagePlugin.Logger.LogWarning(exception);
              FoundFootagePlugin.Logger.LogWarning($"Failed to upload {videoID}, retrying...");
              Thread.Sleep(5000);
            }
          }
        } else {
          FoundFootagePlugin.Logger.LogError($"Failed to extract {videoID}");
        }
      } catch(Exception exception) {
        FoundFootagePlugin.Logger.LogError(exception);
      }
    }).Start();
  }

  private static byte[] SerializeContentBuffer(CameraRecording recording) {
    ContentBuffer buffer = new ContentBuffer();
    foreach(Clip clip in recording.GetAllClips()) {
      if(!clip.Valid) continue;
      if(clip.TryGetContentBuffer(out var contentBuffer)) {
        buffer.AddBuffer(contentBuffer);
      } else {
        FoundFootagePlugin.Logger.LogWarning($"No content buffer found for clip: {clip.clipID}");
      }
    }

    BinarySerializer serializer = new BinarySerializer(512, Allocator.Temp);
    buffer.Serialize(serializer);
    return serializer.buffer.ToArray();
  }
}
