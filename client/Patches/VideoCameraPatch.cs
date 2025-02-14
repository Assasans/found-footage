using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MyceliumNetworking;
using Photon.Pun;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace FoundFootage.Patches;

[HarmonyPatch(typeof(VideoCamera))]
internal static class VideoCameraPatch {
  [HarmonyTranspiler]
  [HarmonyPatch("Update")]
  public static IEnumerable<CodeInstruction> SuppressUpdateObjective(IEnumerable<CodeInstruction> instructions) {
    bool found = false;
    bool runAtNext = false;
    foreach(var instruction in instructions) {
      yield return instruction;

      // IL_025f: isinst       FilmSomethingScaryObjective
      if(instruction.Is(OpCodes.Isinst, typeof(FilmSomethingScaryObjective))) {
        runAtNext = true;
        continue;
      }

      // IL_0264: brfalse.s    IL_0275
      if(runAtNext) {
        found = true;
        runAtNext = false;
        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return CodeInstruction.LoadField(typeof(VideoCamera), "m_recorderInfoEntry");
        yield return CodeInstruction.LoadField(typeof(VideoInfoEntry), "videoID");
        yield return CodeInstruction.LoadField(typeof(VideoHandle), "id");
        yield return CodeInstruction.Call(typeof(GuidUtils), nameof(GuidUtils.IsLocal), new[] { typeof(Guid) });
        yield return new CodeInstruction(OpCodes.Brtrue_S, instruction.operand);
      }
    }

    if(!found)
      FoundFootagePlugin.Logger.LogError(
        "Cannot find PhotonGameLobbyHandler.SetCurrentObjective in VideoCamera.Update");
  }

  [HarmonyPostfix]
  [HarmonyPatch("ConfigItem")]
  public static void InitCamera(VideoCamera __instance, ItemInstanceData data, PhotonView? playerView) {
    FoundFootagePlugin.Logger.LogInfo("InitCamera shitting from player");
    if(data.TryGetEntry(out VideoInfoEntry entry)) {
      FoundFootagePlugin.Logger.LogInfo($"Handling {entry.videoID}");

      if(entry.videoID.id == VideoHandle.Invalid.id) return;
      FoundFootagePlugin.Logger.LogInfo("Check IsLocal");
      if(!GuidUtils.IsLocal(entry.videoID.id)) return;

      FoundFootagePlugin.Logger.LogInfo("Check FakeVideos");
      if(!FoundFootagePlugin.Instance.FakeVideos.Contains(entry.videoID)) return;

      entry.maxTime = 1;
      entry.timeLeft = 0;
      entry.SetDirty();

      var screen = (MeshRenderer)typeof(VideoCamera)
        .GetField("m_cameraScreen", BindingFlags.Instance | BindingFlags.NonPublic)
        .GetValue(__instance);
      screen.enabled = false;

      FoundFootagePlugin.Logger.LogInfo("Check playerView");
      if(playerView == null) return;

      FoundFootagePlugin.Logger.LogInfo("Check ProcessedFakeVideos");
      if(FoundFootagePlugin.Instance.ProcessedFakeVideos.Contains(entry.videoID)) return;
      FoundFootagePlugin.Instance.ProcessedFakeVideos.Add(entry.videoID);

      FoundFootagePlugin.Logger.LogInfo("CreateFakeVideo start");
      var parameters = FoundFootagePlugin.Instance.FoundVideoSearchParamsParsed.ToList();
      FoundFootagePlugin.Logger.LogInfo($"FoundVideoSearchParamsParsed: {string.Join(", ", parameters)}");
      var requestParams = new GetSignedVideoRequest {
        day = parameters.Contains(FoundVideoSearchParam.DAY) ? SurfaceNetworkHandler.RoomStats.CurrentDay : 0,
        playerCount = parameters.Contains(FoundVideoSearchParam.PLAYERS_COUNT)
          ? Object.FindObjectsOfType<Player>().Count(pl => !pl.ai)
          : 0,
        reason = new Random().NextDouble() < FoundFootagePlugin.Instance.DeathVideoChance.Value ? "death" : "extract",
        language = parameters.Contains(FoundVideoSearchParam.LANGUAGE)
          ? CultureInfo.InstalledUICulture.TwoLetterISOLanguageName
          : null
      };

      // new Thread(() => {
      FoundFootagePlugin.Instance.StartCoroutine(DownloadFakeVideoSignedUrl_Unity(requestParams, result => {
        if(result.Error != null) {
          FoundFootagePlugin.Logger.LogError($"An error occurred while getting signed URL: {result.Error}");
          return;
        }

        var response = result.Ok!;
        FoundFootagePlugin.Logger.LogInfo(
          $"Signed URL got successfully (video ID: {response.videoId}): {response.url}");
        FoundFootagePlugin.Logger.LogInfo("Sending signed URL over Mycelium...");
        MyceliumNetwork.RPC(
          FoundFootagePlugin.ModId,
          nameof(FoundFootagePlugin.CreateFakeVideo),
          ReliableType.Reliable,
          entry.videoID.id.ToString(),
          JsonUtility.ToJson(response)
        );
      }));
      // GetSignedVideoResponse response = DownloadFakeVideoSignedUrl(FoundFootagePlugin.Instance, requestParams)
      //   .GetAwaiter().GetResult();
      // }).Start();

      // FoundFootagePlugin.Logger.LogInfo("CreateFakeVideo start");
      // CreateFakeVideo(entry.videoID, content);
      // FoundFootagePlugin.Logger.LogInfo("CreateFakeVideo end");
    } else {
      FoundFootagePlugin.Logger.LogError("No VideoInfoEntry found");
      throw new Exception();
    }
  }

  private static IEnumerator DownloadFakeVideoSignedUrl_Unity(
    GetSignedVideoRequest requestParams,
    Action<Result<GetSignedVideoResponse, Exception>> callback
  ) {
    using UnityWebRequest request = UnityWebRequest.Post(
      $"{FoundFootagePlugin.Instance.ServerUrl.Value}/v3/video/signed?local={PluginInfo.PLUGIN_VERSION}",
      JsonUtility.ToJson(requestParams),
      "application/json"
    );

    yield return request.SendWebRequest();
    if(request.result != UnityWebRequest.Result.Success) {
      // What the fuck should I return?
      callback(Result<GetSignedVideoResponse, Exception>.NewError(new Exception(
        $"request.GetError={request.GetError()} request.error={request.error} downloadHandler.GetErrorMsg={request.downloadHandler.GetErrorMsg()} downloadHandler.error={request.downloadHandler.error}"
      )));
    } else {
      var responseParams = JsonUtility.FromJson<GetSignedVideoResponse>(request.downloadHandler.text);
      callback(Result<GetSignedVideoResponse, Exception>.NewOk(responseParams));
    }
  }

  public static async Task<GetSignedVideoResponse> DownloadFakeVideoSignedUrl(MonoBehaviour target,
    GetSignedVideoRequest requestParams) {
    try {
      return await UnityShit.CoroutineToTask<GetSignedVideoResponse>(target,
        callback => DownloadFakeVideoSignedUrl_Unity(requestParams, callback));
    } catch(Exception exception) {
      FoundFootagePlugin.Logger.LogError($"An error occurred while getting signed URL: {exception}");
      throw;
    }
  }

  public static IEnumerator DownloadFakeVideo_Unity(string signedUrl, Action<Result<byte[], Exception>> callback) {
    using UnityWebRequest request = UnityWebRequest.Get(signedUrl);
    yield return request.SendWebRequest();
    if(request.result != UnityWebRequest.Result.Success) {
      // What the fuck should I return?
      callback(Result<byte[], Exception>.NewError(new Exception(
        $"request.GetError={request.GetError()} request.error={request.error} downloadHandler.GetErrorMsg={request.downloadHandler.GetErrorMsg()} downloadHandler.error={request.downloadHandler.error}"
      )));
    } else {
      callback(Result<byte[], Exception>.NewOk(request.downloadHandler.data.ToArray()));
    }
  }

  public static async Task<byte[]> DownloadFakeVideo(MonoBehaviour target, string signedUrl) {
    try {
      return await UnityShit.CoroutineToTask<byte[]>(target, callback => DownloadFakeVideo_Unity(signedUrl, callback));
    } catch(Exception exception) {
      FoundFootagePlugin.Logger.LogError($"An error occurred while downloading the file: {exception}");
      throw;
    }
  }

  public static void CreateFakeVideo(VideoHandle handle, byte[] content) {
    FoundFootagePlugin.Logger.LogInfo("m_recordings");
    var recordings = (Dictionary<VideoHandle, CameraRecording>)typeof(RecordingsHandler)
      .GetField("m_recordings", BindingFlags.Instance | BindingFlags.NonPublic)
      .GetValue(RecordingsHandler.Instance);

    FoundFootagePlugin.Logger.LogInfo("CreateNewRecording");
    CameraRecording recording =
      (CameraRecording)typeof(RecordingsHandler).GetMethod("CreateNewRecording",
          BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(RecordingsHandler.Instance, new object[] { handle });
    FoundFootagePlugin.Logger.LogInfo($"recording: {recording}");
    var clip = new Clip(new ClipID(new Guid()), true, PhotonNetwork.LocalPlayer.ActorNumber, recording);
    clip.isRecording = false;
    clip.encoded = true;
    clip.local = true;
    if(FoundFootagePlugin.Instance.FakeContentBuffers.TryGetValue(handle, out var buffer)) {
      clip.SetContentBufffer(buffer);
      FoundFootagePlugin.Logger.LogInfo("Set fake content buffer from remote");
    } else {
      clip.SetContentBufffer(new ContentBuffer());
    }

    clip.SetValid(true);
    recording.AddNewClip(clip);

    var directory = clip.GetClipDirectory();
    Directory.CreateDirectory(directory);

    var path = Path.Combine(directory, "output.webm");
    using(var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) {
      fileStream.Write(content.AsSpan());
    }

    // Copy to result file in case some mod (MoreCameras) breaks extraction again, this is not needed in vanilla.
    // MoreCameras issue was fixed by settings [Clip#local] to true.
    var fullPath = Path.Combine(directory, "../fullRecording.webm");
    using(var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
      fileStream.Write(content.AsSpan());
    }

    if(!recordings.ContainsKey(handle)) {
      FoundFootagePlugin.Logger.LogInfo("m_recordings.Add()");
      recordings.Add(handle, recording);
    }

    FoundFootagePlugin.Logger.LogInfo("InitCamera shitted");
  }
}
