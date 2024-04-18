using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using MyceliumNetworking;
using Photon.Pun;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.PropertyVariants;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace FoundFootage;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public class FoundFootagePlugin : BaseUnityPlugin {
  public const uint ModId = 0x20d82112;

  public static FoundFootagePlugin Instance { get; private set; }
  internal static ManualLogSource Logger { get; private set; }

  internal Random Random { get; private set; }
  internal List<VideoHandle> FakeVideos { get; private set; }
  internal List<VideoHandle> ProcessedFakeVideos { get; private set; }
  internal Dictionary<VideoHandle, string> ClientToServerId { get; private set; }

  internal ConfigEntry<string>? ServerUrl { get; private set; }
  internal ConfigEntry<string>? UserId { get; private set; }
  internal ConfigEntry<bool>? WarningShown { get; private set; }

  internal ConfigEntry<bool>? VotingEnabled { get; private set; }

  internal ConfigEntry<float>? SpawnChance { get; private set; }
  internal ConfigEntry<float>? DeathUploadChance { get; private set; }
  internal ConfigEntry<float>? PassUploadChance { get; private set; }

  private void Awake() {
    Instance = this;
    Logger = base.Logger;

    Random = new Random();
    FakeVideos = new List<VideoHandle>();
    ProcessedFakeVideos = new List<VideoHandle>();
    ClientToServerId = new Dictionary<VideoHandle, string>();

    ServerUrl = Config.Bind("Internal", "ServerUrl", "https://foundfootage-server.assasans.dev",
      "Base URL of the server hosting the videos.\n### DATA REQUEST OR REMOVAL ###\nIf you want to request your data or have it removed:\nsend an email to the address available on the \"/info\" endpoint (e.g. https://server.local/info).\nNote that your email must include your User ID (see below).");
    UserId = Config.Bind("Internal", "UserId", "",
      "Random identifier associated with uploaded recordings. Can be used to remove your data.");
    if(UserId.Value == (string)UserId.DefaultValue) {
      UserId.Value = Guid.NewGuid().ToString();
      Config.Save();
      Logger.LogInfo($"Generated user UUID: {UserId.Value}");
    }

    WarningShown = Config.Bind("Internal", "WarningShown", false, "");

    VotingEnabled = Config.Bind("Voting", "VotingEnabled", true, "Enable voting after watching another team's video.");

    SpawnChance = Config.Bind("Chances", "SpawnChance", 0.3f,
      "Chance that another team's camera will be spawned (0 to disable).");
    DeathUploadChance = Config.Bind("Chances", "DeathUploadChance", 1f,
      "Chance that the camera video will be uploaded when all team members die (1 - always, 0 to disable).");
    PassUploadChance = Config.Bind("Chances", "PassUploadChance", 1f,
      "Chance that the camera video will be uploaded when the team returns to surface (1 - always, 0 to disable).");

    // Plugin startup logic
    Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
    Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), PluginInfo.PLUGIN_GUID);
    Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} patched!");
    Logger.LogInfo($"Server URL: {Instance.ServerUrl?.Value}");

    try {
      MyceliumNetwork.RegisterNetworkObject(this, ModId);
    } catch(Exception exception) {
      Logger.LogInfo(exception);
    }

    if(!WarningShown.Value) {
      WarningShown.Value = true;
      Config.Save();

      Task.Run(() => {
        // Give game time to create window
        Thread.Sleep(1000);
        MessageBoxUtils.Show(
          "Content Warning - Found Footage",
          "READ THIS IMPORTANT INFORMATION!\n\n" +
          "This mod sends your in-game camera footage to the server along with audio, depending on the mod's configuration. ANYONE CAN WATCH YOUR VIDEOS LATER!\n" +
          "You can request your data or have it removed, see configuration file for more information.\n\n" +
          "This message will only be shown once.",
          0x00000030
        );
      });
    }

    Task.Run(async () => {
      try {
        VersionChecker checker = new VersionChecker();
        string remote = await checker.GetVersion();
        if(remote == VersionChecker.IncompatibleVersion) {
          // Try to change to new URL
          if(ServerUrl.Value == "https://foundfootage-server.assasans.dev") {
            ServerUrl.Value = (string)ServerUrl.DefaultValue;
            Logger.LogInfo($"Updated server URL to {ServerUrl.Value}");

            remote = await checker.GetVersion();
          }
        }

        string local = PluginInfo.PLUGIN_VERSION;
        if(checker.IsCompatibleWith(remote, local)) {
          Logger.LogInfo($"Local version {local} is compatible with remote {remote}");
        } else {
          Logger.LogError($"Local version {local} is NOT compatible with remote {remote}");
          MessageBoxUtils.Show(
            "Content Warning - Found Footage",
            $"Incompatible mod version! Local: {local}, remote: {remote}\nYou can play the game, but beware of undefined behaviour. If possible, please update the mod.",
            0x00000010
          );
        }
      } catch(Exception exception) {
        Logger.LogError(exception);
        throw;
      }
    });
  }

  [CustomRPC]
  public void CreateFakeVideo(string guidString, string signedUrl, string? serverVideoId = null) {
    Logger.LogInfo("CreateFakeVideo!");
    VideoHandle handle = new VideoHandle(Guid.Parse(guidString));

    if(serverVideoId != null) {
      ClientToServerId.Add(handle, serverVideoId);
    }

    new Thread(() => {
      Logger.LogInfo($"Downloading video {guidString} -> {signedUrl}");
      byte[] content = VideoCameraPatch.DownloadFakeVideo(signedUrl).GetAwaiter().GetResult();

      Logger.LogInfo($"VideoCameraPatch.CreateFakeVideo");
      VideoCameraPatch.CreateFakeVideo(handle, content);
    }).Start();
  }
}

public static class HttpUtils {
  public static void UploadFile(string url, string filePath, Dictionary<String, String> properties) {
    string boundary = "---------------------------" + DateTime.Now.Ticks.ToString("x");
    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
    request.Method = "PUT";
    request.ContentType = "multipart/form-data; boundary=" + boundary;

    using(Stream requestStream = request.GetRequestStream()) {
      // Write boundary and file header
      WriteBoundary(requestStream, boundary);
      foreach(var (key, value) in properties) {
        WriteFormValue(requestStream, key, value, boundary);
      }

      // Write file content
      WriteFile(requestStream, filePath, boundary);

      // Write end boundary
      byte[] endBoundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");
      requestStream.Write(endBoundaryBytes, 0, endBoundaryBytes.Length);
    }

    try {
      using WebResponse response = request.GetResponse();
      using Stream responseStream = response.GetResponseStream();
      using StreamReader reader = new StreamReader(responseStream);
      string responseText = reader.ReadToEnd();
      FoundFootagePlugin.Logger.LogInfo(responseText);
    } catch(WebException exception) {
      FoundFootagePlugin.Logger.LogError($"Error: {exception}");
    }
  }

  public static void WriteBoundary(Stream requestStream, string boundary) {
    byte[] boundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\n");
    requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
  }

  public static void WriteFormValue(Stream requestStream, string fieldName, string value, string boundary) {
    string formItemTemplate = "Content-Disposition: form-data; name=\"{0}\"\r\n\r\n{1}";
    string formItem = string.Format(formItemTemplate, fieldName, value);
    byte[] formItemBytes = Encoding.UTF8.GetBytes(formItem);
    requestStream.Write(formItemBytes, 0, formItemBytes.Length);
    WriteBoundary(requestStream, boundary);
  }

  public static void WriteFile(Stream requestStream, string filePath, string boundary) {
    using(FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read)) {
      string headerTemplate =
        "Content-Disposition: form-data; name=\"file\"; filename=\"{0}\"\r\nContent-Type: application/octet-stream\r\n\r\n";
      string header = string.Format(headerTemplate, Path.GetFileName(filePath));
      byte[] headerBytes = Encoding.UTF8.GetBytes(header);
      requestStream.Write(headerBytes, 0, headerBytes.Length);

      byte[] buffer = new byte[1024 * 1024]; // 1MB buffer
      int bytesRead;
      while((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) != 0) {
        requestStream.Write(buffer, 0, bytesRead);
      }
    }
  }
}

// [HarmonyPatch(typeof(SurfaceNetworkHandler))]
// internal static class SurfaceNetworkHandlerPatch {
//   [HarmonyPrefix]
//   [HarmonyPatch("ReturningFromLostWorld", MethodType.Getter)]
//   internal static bool ReturningFromLostWorld(ref bool __result) {
//     // FoundFootagePlugin.Logger.LogError("ReturningFromLostWorld!");
//     __result = true;
//     return false;
//   }
//
//   [HarmonyPostfix]
//   [HarmonyPatch("InitSurface")]
//   internal static void SpawnOnStartRun() {
//     SpawnExtraCamera();
//   }
//
//   private static void SpawnExtraCamera() {
//     FoundFootagePlugin.Logger.LogInfo("Spawning extra camera!");
//
//     ItemInstanceData instance = new ItemInstanceData(Guid.NewGuid());
//     VideoInfoEntry entry = new VideoInfoEntry();
//     entry.videoID = new VideoHandle(GuidUtils.MakeLocal(Guid.NewGuid()));
//     entry.maxTime = 1;
//     entry.timeLeft = 0;
//     entry.SetDirty();
//     instance.AddDataEntry(entry);
//     FoundFootagePlugin.Instance.FakeVideos.Add(entry.videoID);
//     FoundFootagePlugin.Logger.LogInfo($"added entry {entry.videoID.id}");
//     Pickup pickup = PickupHandler.CreatePickup(1, instance, new Vector3(-14.842f, 2.418f, 8.776f),
//       Quaternion.Euler(0f, -67.18f, 0f));
//     FoundFootagePlugin.Logger.LogInfo("Spawned extra camera!");
//
//     if(CameraHandler.TryGetCamera(instance.m_guid, out VideoCamera camera)) {
//     } else {
//       FoundFootagePlugin.Logger.LogError("No VideoCamera found");
//     }
//   }
// }

public static class GuidUtils {
  public static Guid MakeLocal(Guid guid) {
    var property = typeof(Guid).GetField("_a", BindingFlags.Instance | BindingFlags.NonPublic);
    object boxed = guid;
    property.SetValue(boxed, int.MaxValue);
    return (Guid)boxed;
  }

  public static bool IsLocal(Guid guid) {
    return (int)typeof(Guid)
      .GetField("_a", BindingFlags.Instance | BindingFlags.NonPublic)
      .GetValue(guid) == int.MaxValue;
  }
}

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
      new Thread(() => {
        (string signedUrl, string? serverVideoId) = DownloadFakeVideoSignedUrl().GetAwaiter().GetResult();
        FoundFootagePlugin.Logger.LogInfo("Sending signed URL over Mycelium...");
        MyceliumNetwork.RPC(
          FoundFootagePlugin.ModId,
          nameof(FoundFootagePlugin.CreateFakeVideo),
          ReliableType.Reliable,
          entry.videoID.id.ToString(),
          signedUrl,
          serverVideoId
        );
      }).Start();

      // FoundFootagePlugin.Logger.LogInfo("CreateFakeVideo start");
      // CreateFakeVideo(entry.videoID, content);
      // FoundFootagePlugin.Logger.LogInfo("CreateFakeVideo end");
    } else {
      FoundFootagePlugin.Logger.LogError("No VideoInfoEntry found");
      throw new Exception();
    }
  }

  public static async Task<(string, string?)> DownloadFakeVideoSignedUrl() {
    using var httpClient = new HttpClient();
    try {
      using var response = await httpClient.GetAsync(
        $"{FoundFootagePlugin.Instance.ServerUrl.Value}/video/signed",
        HttpCompletionOption.ResponseHeadersRead
      );
      response.EnsureSuccessStatusCode();
      var signedUrl = await response.Content.ReadAsStringAsync();

      string? videoId = null;
      if(response.Headers.TryGetValues("X-Video-Id", out var values)) {
        videoId = values.Single();
      }

      FoundFootagePlugin.Logger.LogInfo($"Signed URL got successfully (video ID: {videoId}): {signedUrl}");
      return (signedUrl, videoId);
    } catch(HttpRequestException exception) {
      FoundFootagePlugin.Logger.LogError($"An error occurred while getting signed URL: {exception}");
      throw;
    }
  }

  public static async Task<byte[]> DownloadFakeVideo(string signedUrl) {
    using var httpClient = new HttpClient();
    try {
      using var response = await httpClient.GetAsync(signedUrl, HttpCompletionOption.ResponseHeadersRead);
      response.EnsureSuccessStatusCode();

      using var stream = new MemoryStream();
      await response.Content.CopyToAsync(stream);
      FoundFootagePlugin.Logger.LogInfo("File downloaded successfully.");
      return stream.ToArray();
    } catch(HttpRequestException exception) {
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
    clip.SetContentBufffer(new ContentBuffer());
    recording.AddNewClip(clip);

    var directory = clip.GetClipDirectory();
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "output.webm");

    using(var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) {
      fileStream.Write(content.AsSpan());
    }

    if(!recordings.ContainsKey(handle)) {
      FoundFootagePlugin.Logger.LogInfo("m_recordings.Add()");
      recordings.Add(handle, recording);
    }

    FoundFootagePlugin.Logger.LogInfo("InitCamera shitted");
  }
}

public class VersionChecker {
  public static readonly string IncompatibleVersion = "0.0.0 (incompatible)";

  public async Task<string> GetVersion() {
    using var httpClient = new HttpClient();
    try {
      using var response = await httpClient.GetAsync(
        $"{FoundFootagePlugin.Instance.ServerUrl.Value}/version?local={PluginInfo.PLUGIN_VERSION}",
        HttpCompletionOption.ResponseHeadersRead);
      response.EnsureSuccessStatusCode();
      return await response.Content.ReadAsStringAsync();
    } catch(HttpRequestException exception) {
      FoundFootagePlugin.Logger.LogError($"An error occurred while fetching version: {exception}");
      return IncompatibleVersion;
    }
  }

  public bool IsCompatibleWith(string compatible, string actual) {
    string[] components = compatible.Split('(');
    if(components[0].Trim() == actual) return true;
    if(components[1].Contains($"{actual}-compatible")) return true;
    return false;
  }
}

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

[HarmonyPatch(typeof(VerboseDebug))]
internal static class VerboseDebugPatch {
  [HarmonyPostfix]
  [HarmonyPatch("Log")]
  internal static void Log(string message) {
    Debug.Log(message);
  }

  [HarmonyPostfix]
  [HarmonyPatch("LogError")]
  internal static void LogError(string s) {
    Debug.LogError(s);
  }
}

[HarmonyPatch(typeof(ExtractVideoMachine))]
internal static class ExtractVideoMachinePatch {
  [HarmonyPostfix]
  [HarmonyPatch("RPC_Success")]
  internal static void RPC_Success() {
    FoundFootagePlugin.Logger.LogInfo("RPC_Success shitted");
    if(!PhotonNetwork.IsMasterClient) return;

    var recordings = RecordingsHandler.GetRecordings();
    foreach(var (videoID, recording) in recordings) {
      FoundFootagePlugin.Logger.LogInfo($"Check {videoID}");
      if(GuidUtils.IsLocal(videoID.id)) continue;

      if(FoundFootagePlugin.Instance.Random.NextDouble() <= FoundFootagePlugin.Instance.PassUploadChance.Value) {
        PhotonGameLobbyHandlerPatch.UploadRecording(videoID, recording, "extract");
      } else {
        FoundFootagePlugin.Logger.LogInfo("Do not extracting");
      }
    }
  }
}

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

      __instance.StartCoroutine(WaitThen(5f, () => {
        var recordings = RecordingsHandler.GetRecordings();
        foreach(var (videoID, recording) in recordings) {
          FoundFootagePlugin.Logger.LogInfo($"Check {videoID}");
          if(GuidUtils.IsLocal(videoID.id)) continue;

          if(FoundFootagePlugin.Instance.Random.NextDouble() <= FoundFootagePlugin.Instance.DeathUploadChance.Value) {
            UploadRecording(videoID, recording, "death");
          } else {
            FoundFootagePlugin.Logger.LogInfo("Do not extracting");
          }
        }

        FoundFootagePlugin.Logger.LogInfo("CheckForAllDead shitted");
      }));
    }
  }

  public static void UploadRecording(VideoHandle videoID, CameraRecording recording, string reason) {
    new Thread(() => {
      try {
        bool success = false;
        for(int attempt = 0; attempt < 30; attempt++) {
          success |= RecordingsHandler.Instance.ExtractRecording(recording);
          if(success) break;

          Thread.Sleep(5000);
          FoundFootagePlugin.Logger.LogWarning($"Failed to extract {videoID}, retrying...");
        }

        if(success) {
          var path = Path.Combine(recording.GetDirectory(), "fullRecording.webm");
          FoundFootagePlugin.Logger.LogInfo($"Extracted {videoID}: {path}");
          HttpUtils.UploadFile($"{FoundFootagePlugin.Instance.ServerUrl.Value}/videos", path,
            new Dictionary<string, string> {
              ["video_id"] = recording.videoHandle.id.ToString(),
              ["user_id"] = FoundFootagePlugin.Instance.UserId.Value,
              ["lobby_id"] = PhotonNetwork.CurrentRoom.Name,
              ["language"] = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName,
              ["reason"] = reason
            });
          FoundFootagePlugin.Logger.LogInfo("Uploaded video!");
        } else {
          FoundFootagePlugin.Logger.LogError($"Failed to extract {videoID}");
        }
      } catch(Exception exception) {
        FoundFootagePlugin.Logger.LogError(exception);
      }
    }).Start();
  }
}

[HarmonyPatch(typeof(UploadVideoStation))]
internal static class UploadVideoStationPatch {
  public static GameObject? LikeUi;
  public static GameObject? DislikeUi;
  public static GameObject? LikeInteractable;
  public static GameObject? DislikeInteractable;

  [HarmonyPostfix]
  [HarmonyPatch("Awake")]
  public static void Awake(UploadVideoStation __instance) {
    if(!FoundFootagePlugin.Instance.VotingEnabled.Value) {
      FoundFootagePlugin.Logger.LogInfo("Voting is disabled, not patching UploadVideoStation.Awake");
      return;
    }

    FoundFootagePlugin.Logger.LogError($"Root: {__instance.gameObject}");

    var videoDone = __instance.m_uploadCompleteUI.gameObject.transform.Find("VIDEO/VideoDone").gameObject;
    var saveVideo = videoDone.transform.Find("SaveVideo").gameObject;
    FoundFootagePlugin.Logger.LogError($"SaveVideo: {saveVideo}");

    var interactableRoot = __instance.gameObject.transform.Find("SaveVideoToDesktopInteractable").gameObject;
    foreach(var collider in interactableRoot.GetComponentsInChildren<BoxCollider>()) {
      collider.size = Vector3.zero;
    }

    var replayInteractable = interactableRoot.transform.Find("ReplayInt").gameObject;
    FoundFootagePlugin.Logger.LogError($"ReplayInt: {replayInteractable}");

    // Unity is fucked up
    __instance.StartCoroutine(PhotonGameLobbyHandlerPatch.WaitThen(5f, () => {
      Sprite? likeSprite = null;
      Sprite? dislikeSprite = null;

      var objects = Resources.FindObjectsOfTypeAll<Object>();
      foreach(var it in objects) {
        if(it is Sprite sprite) {
          if(it.name == "Emoji-Great--Streamline-Ultimate") {
            FoundFootagePlugin.Logger.LogInfo($"Found like sprite: {sprite}");
            likeSprite = sprite;
          }

          if(it.name == "Skull-1--Streamline-Ultimate (1)") {
            FoundFootagePlugin.Logger.LogInfo($"Found dislike sprite: {sprite}");
            dislikeSprite = sprite;
          }
        }
      }

      var like = Object.Instantiate(saveVideo, videoDone.transform);
      like.name = "Like";
      like.transform.localPosition = new Vector3(-178.82f, -178.82f, 0);
      Object.Destroy(like.GetComponentInChildren<GameObjectLocalizer>());
      like.GetComponentInChildren<TextMeshProUGUI>().SetText("LIKE");
      like.GetComponentInChildren<Image>().sprite = likeSprite;
      LikeUi = like;

      var dislike = Object.Instantiate(saveVideo, videoDone.transform);
      dislike.name = "Dislike";
      dislike.transform.localPosition = new Vector3(0, -178.82f, 0);
      Object.Destroy(dislike.GetComponentInChildren<GameObjectLocalizer>());
      dislike.GetComponentInChildren<TextMeshProUGUI>().SetText("DISLIKE");
      dislike.GetComponentInChildren<Image>().sprite = dislikeSprite;
      DislikeUi = dislike;

      var likeInteractable = Object.Instantiate(replayInteractable, interactableRoot.transform);
      likeInteractable.name = "LikeInt";
      likeInteractable.transform.localPosition = new Vector3(0, -0.5772f, 0);
      Object.Destroy(likeInteractable.GetComponent<ReplayVideoInteractable>());
      likeInteractable.AddComponent<LikeInteractable>();
      LikeInteractable = likeInteractable;

      var dislikeInteractable = Object.Instantiate(replayInteractable, interactableRoot.transform);
      dislikeInteractable.name = "DisikeInt";
      dislikeInteractable.transform.localPosition = new Vector3(1.0727f, -0.5772f, 0);
      Object.Destroy(dislikeInteractable.GetComponent<ReplayVideoInteractable>());
      dislikeInteractable.AddComponent<DislikeInteractable>();
      DislikeInteractable = dislikeInteractable;
    }));
  }
}

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
