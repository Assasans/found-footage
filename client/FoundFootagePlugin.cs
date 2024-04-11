using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using DefaultNamespace;
using HarmonyLib;
using MyceliumNetworking;
using Photon.Pun;
using Photon.Realtime;
using Unity.Collections;
using UnityEngine;
using Zorro.Core;
using Zorro.Core.Serizalization;
using Zorro.PhotonUtility;
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

  internal ConfigEntry<string>? ServerUrl { get; private set; }
  internal ConfigEntry<string>? UserId { get; private set; }
  internal ConfigEntry<bool>? WarningShown { get; private set; }

  internal ConfigEntry<float>? SpawnChance { get; private set; }
  internal ConfigEntry<float>? DeathUploadChance { get; private set; }
  internal ConfigEntry<float>? PassUploadChance { get; private set; }

  private void Awake() {
    Instance = this;
    Logger = base.Logger;

    Random = new Random();
    FakeVideos = new List<VideoHandle>();
    ProcessedFakeVideos = new List<VideoHandle>();

    ServerUrl = Config.Bind("Internal", "ServerUrl", "https://foundfootage-server.assasans.workers.dev",
      "Base URL of the server hosting the videos.\n### DATA REQUEST OR REMOVAL ###\nIf you want to request your data or have it removed:\nsend an email to the address available on the \"/info\" endpoint (e.g. https://server.local/info).\nNote that your email must include your User ID (see below).");
    UserId = Config.Bind("Internal", "UserId", "",
      "Random identifier associated with uploaded recordings. Can be used to remove your data.");
    if(UserId.Value == (string)UserId.DefaultValue) {
      UserId.Value = Guid.NewGuid().ToString();
      Config.Save();
      Logger.LogInfo($"Generated user UUID: {UserId.Value}");
    }

    WarningShown = Config.Bind("Internal", "WarningShown", false, "");

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

    try {
      MyceliumNetwork.RegisterNetworkObject(this, ModId);
    } catch(Exception exception) {
      Logger.LogInfo(exception);
    }

    if(!WarningShown.Value) {
      WarningShown.Value = true;
      Config.Save();

      MessageBoxUtils.Show(
        "Content Warning - Found Footage",
        "READ THIS IMPORTANT INFORMATION!\n\n" +
        "This mod sends your in-game camera footage to the server along with audio, depending on the mod's configuration. " +
        "You can request your data or have it removed, see configuration file for more information.\n\n" +
        "This message will only be shown once.",
        0x00000030
      );
    }

    Task.Run(async () => {
      try {
        VersionChecker checker = new VersionChecker();
        string remote = await checker.GetVersion();
        string local = PluginInfo.PLUGIN_VERSION;
        if(checker.IsCompatibleWith(remote, local)) {
          Logger.LogInfo($"Local version {local} is compatible with remote {remote}");
        } else {
          Logger.LogError($"Local version {local} is NOT compatible with remote {remote}");
          MessageBoxUtils.Show("Content Warning - Found Footage",
            $"Incompatible mod version! Local: {local}, remote: {remote}\nYou can play the game, but beware of undefined behaviour. If possible, please update the mod.",
            0x00000010);
        }
      } catch(Exception exception) {
        Logger.LogError(exception);
        throw;
      }
    });
  }

  private Dictionary<VideoHandle, MemoryStream> parts = new();

  [CustomRPC]
  public void CreateFakeVideo(string guidString, byte[] content, bool final) {
    Logger.LogError("CreateFakeVideo!");
    VideoHandle handle = new VideoHandle(Guid.Parse(guidString));
    Logger.LogError("CreateFakeVideo!!");

    if(!parts.TryGetValue(handle, out MemoryStream? stream)) {
      parts[handle] = stream = new MemoryStream();
    }

    if(final) {
      Logger.LogError($"Final!!!");
      VideoCameraPatch.CreateFakeVideo(handle, stream.ToArray());
      Logger.LogError("CreateFakeVideo!!!");
    } else {
      stream.Write(content);
      Logger.LogError($"Wrote {content.Length} bytes");
    }
  }
}

internal static class MessageBoxUtils {
  [DllImport("user32.dll", CharSet = CharSet.Auto)]
  private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

  internal static void Show(string title, string content, uint type) {
    MessageBox(IntPtr.Zero, content, title, type);
  }
}

internal static class HttpUtils {
  internal static void UploadFile(string url, string filePath, Dictionary<String, String> properties) {
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
      Console.WriteLine(responseText);
    } catch(WebException ex) {
      Console.WriteLine("Error: " + ex.Message);
    }
  }

  private static void WriteBoundary(Stream requestStream, string boundary) {
    byte[] boundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\n");
    requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
  }

  private static void WriteFormValue(Stream requestStream, string fieldName, string value, string boundary) {
    string formItemTemplate = "Content-Disposition: form-data; name=\"{0}\"\r\n\r\n{1}";
    string formItem = string.Format(formItemTemplate, fieldName, value);
    byte[] formItemBytes = Encoding.UTF8.GetBytes(formItem);
    requestStream.Write(formItemBytes, 0, formItemBytes.Length);
    WriteBoundary(requestStream, boundary);
  }

  private static void WriteFile(Stream requestStream, string filePath, string boundary) {
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
//     entry.maxTime = 0;
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
//       Debug.LogError("No VideoCamera found");
//     }
//   }
// }

internal static class GuidUtils {
  internal static Guid MakeLocal(Guid guid) {
    var property = typeof(Guid).GetField("_a", BindingFlags.Instance | BindingFlags.NonPublic);
    object boxed = guid;
    property.SetValue(boxed, int.MaxValue);
    return (Guid)boxed;
  }

  internal static bool IsLocal(Guid guid) {
    return (int)typeof(Guid)
      .GetField("_a", BindingFlags.Instance | BindingFlags.NonPublic)
      .GetValue(guid) == int.MaxValue;
  }
}

[HarmonyPatch(typeof(VideoCamera))]
internal static class VideoCameraPatch {
  [HarmonyPostfix]
  [HarmonyPatch("ConfigItem")]
  public static void InitCamera(VideoCamera __instance, ItemInstanceData data, PhotonView? playerView) {
    FoundFootagePlugin.Logger.LogInfo($"InitCamera shitting from player");
    if(data.TryGetEntry(out VideoInfoEntry entry)) {
      FoundFootagePlugin.Logger.LogInfo($"Handling {entry.videoID}");

      if(entry.videoID.id == VideoHandle.Invalid.id) return;
      FoundFootagePlugin.Logger.LogInfo($"Check IsLocal");
      if(!GuidUtils.IsLocal(entry.videoID.id)) return;

      FoundFootagePlugin.Logger.LogInfo($"Check FakeVideos");
      if(!FoundFootagePlugin.Instance.FakeVideos.Contains(entry.videoID)) return;

      entry.maxTime = 0;
      entry.timeLeft = 0;
      entry.SetDirty();

      var screen = (MeshRenderer)typeof(VideoCamera)
        .GetField("m_cameraScreen", BindingFlags.Instance | BindingFlags.NonPublic)
        .GetValue(__instance);
      screen.enabled = false;

      FoundFootagePlugin.Logger.LogInfo($"Check playerView");
      if(playerView == null) return;

      FoundFootagePlugin.Logger.LogInfo($"Check ProcessedFakeVideos");
      if(FoundFootagePlugin.Instance.ProcessedFakeVideos.Contains(entry.videoID)) return;
      FoundFootagePlugin.Instance.ProcessedFakeVideos.Add(entry.videoID);

      FoundFootagePlugin.Logger.LogError("CreateFakeVideo start");
      new Thread(() => {
        byte[] content = DownloadFakeVideo().GetAwaiter().GetResult();
        FoundFootagePlugin.Logger.LogError("MyceliumNetwork.RPC start");
        foreach(var chunk in EnumerableExtensions.SplitArray(content, 1024 * 128)) {
          FoundFootagePlugin.Logger.LogError($"Send chunk {chunk.Length} bytes");
          MyceliumNetwork.RPC(FoundFootagePlugin.ModId, nameof(FoundFootagePlugin.CreateFakeVideo),
            ReliableType.Reliable,
            entry.videoID.id.ToString(), chunk, false);
        }

        FoundFootagePlugin.Logger.LogError("Send final");
        MyceliumNetwork.RPC(FoundFootagePlugin.ModId, nameof(FoundFootagePlugin.CreateFakeVideo), ReliableType.Reliable,
          entry.videoID.id.ToString(), Array.Empty<byte>(), true);
      }).Start();

      // FoundFootagePlugin.Logger.LogError("CreateFakeVideo start");
      // CreateFakeVideo(entry.videoID, content);
      // FoundFootagePlugin.Logger.LogError("CreateFakeVideo end");
    } else {
      Debug.LogError("No VideoInfoEntry found");
      throw new Exception();
    }
  }

  public static async Task<byte[]> DownloadFakeVideo() {
    using var httpClient = new HttpClient();
    try {
      using var response = await httpClient.GetAsync($"{FoundFootagePlugin.Instance.ServerUrl.Value}/video",
        HttpCompletionOption.ResponseHeadersRead);
      response.EnsureSuccessStatusCode();

      using var stream = new MemoryStream();
      await response.Content.CopyToAsync(stream);
      Console.WriteLine("File downloaded successfully.");
      return stream.ToArray();
    } catch(HttpRequestException ex) {
      Console.WriteLine($"An error occurred while downloading the file: {ex.Message}");
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
    Debug.LogError($"recording: {recording}");
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

    // RecordingsHandler.Instance.ExtractRecording()
    if(!recordings.ContainsKey(handle)) {
      FoundFootagePlugin.Logger.LogInfo("m_recordings.Add()");
      recordings.Add(handle, recording);
    }

    // SendVideoToOthers(clip);

    FoundFootagePlugin.Logger.LogInfo("InitCamera shitted");
  }

  private static void SendVideoToOthers(Clip clip) {
    FoundFootagePlugin.Logger.LogInfo("SendVideoToOthers entered");
    var sender = new ClipSendUtils();
    sender.OnCreated();
    sender.InitSendVideoHandler();
    sender.SendVideoThroughPhoton(clip, false);
    FoundFootagePlugin.Logger.LogError($"Fake clip: {clip.clipID} has sent!");
    CustomCommands<CustomCommandType>.SendPackage(new SendClipCompletedPackage {
      ClipID = clip.clipID,
      VideoHandle = clip.m_recording.videoHandle
    }, ReceiverGroup.All);
  }
}

public class VersionChecker {
  public async Task<string> GetVersion() {
    using var httpClient = new HttpClient();
    try {
      using var response = await httpClient.GetAsync($"{FoundFootagePlugin.Instance.ServerUrl.Value}/version",
        HttpCompletionOption.ResponseHeadersRead);
      response.EnsureSuccessStatusCode();
      return await response.Content.ReadAsStringAsync();
    } catch(HttpRequestException exception) {
      Console.WriteLine($"An error occurred while fetching version: {exception.Message}");
      return "0.0.0 (incompatible)";
    }
  }

  public bool IsCompatibleWith(string compatible, string actual) {
    string[] components = compatible.Split('(');
    if(components[0].Trim() == actual) return true;
    if(components[1].Contains($"{actual}-compatible")) return true;
    return false;
  }
}

internal class ClipSendUtils {
  private readonly string VIDEO_EXTENSION = ".webm";
  private const int BYTES_PER_CHUNK = 30000;
  private string PATH_TO_VIDEO;
  private bool m_UseSteamNetwork;
  private SteamLobbyHandler m_SteamLobby;
  private bool m_Sending;

  public void OnCreated() {
    PATH_TO_VIDEO = RecordingsHandler.GetDirectory();
    InitSendVideoHandler();
  }

  public void InitSendVideoHandler() {
    bool flag = Directory.Exists(PATH_TO_VIDEO);
    FoundFootagePlugin.Logger.LogError("INIT PhotonSendVideoHandler with path: " + PATH_TO_VIDEO + " Exist? " + flag);
    if(!flag)
      Directory.CreateDirectory(PATH_TO_VIDEO);
    m_SteamLobby = MainMenuHandler.SteamLobbyHandler;
    if(m_SteamLobby != null)
      m_UseSteamNetwork = m_SteamLobby.UseSteamNetwork;
    else
      m_UseSteamNetwork = false;
  }

  public void SendVideoThroughPhoton(Clip clipToSend, bool isReRequest) {
    ContentBuffer contentBuffer;
    if(!clipToSend.TryGetContentBuffer(out contentBuffer)) {
      FoundFootagePlugin.Logger.LogError("FATAL! Clip: " + clipToSend.clipID.id +
                                         " Does not have a content buffer! Cannot send video!");
    } else {
      VideoHandle videoHandle = clipToSend.m_recording.videoHandle;
      FoundFootagePlugin.Logger.LogError("Sending Video Through Photon: CLIP: " + clipToSend.clipID.id + " VIDEO: " +
                                         videoHandle);
      m_Sending = true;
      string path = Path.Combine(clipToSend.GetClipDirectory(), "output.webm");
      byte[] sourceArray = File.ReadAllBytes(path);
      FoundFootagePlugin.Logger.LogError("Found video: " + path + " Bytes: " + sourceArray.Length);
      int length = BYTES_PER_CHUNK;
      int sourceIndex = 0;
      int num = 0;
      List<byte[]> videoChunks = new List<byte[]>();
      for(int index = 0; index < 10000; ++index) {
        if(sourceIndex + length > sourceArray.Length) {
          byte[] destinationArray = new byte[sourceArray.Length - sourceIndex];
          Array.Copy(sourceArray, sourceIndex, destinationArray, 0, destinationArray.Length);
          videoChunks.Add(destinationArray);
          num += destinationArray.Length;
          break;
        }

        byte[] destinationArray1 = new byte[length];
        Array.Copy(sourceArray, sourceIndex, destinationArray1, 0, destinationArray1.Length);
        videoChunks.Add(destinationArray1);
        sourceIndex += destinationArray1.Length;
        FoundFootagePlugin.Logger.LogError("Adding Chunk: New Pointer " + sourceIndex + " Wrote: " +
                                           destinationArray1.Length + " Bytes!");
        num += destinationArray1.Length;
      }

      FoundFootagePlugin.Logger.LogError("Chunks made: " + videoChunks.Count + " Bytes written: " + num);
      SendVideoChunks(videoChunks, clipToSend.clipID, videoHandle, contentBuffer, isReRequest);
    }
  }

  private void SendVideoChunks(List<byte[]> videoChunks, ClipID clipID, VideoHandle videoID,
    ContentBuffer contentBuffer, bool isReRequest) {
    FoundFootagePlugin.Logger.LogError("Begin To Send VideoChunks: " + videoChunks.Count + " Clip: " + clipID.id +
                                       " Video: " + videoID);
    ushort chunkIndex = 0;
    foreach(byte[] videoChunk in videoChunks) {
      SendVideoChunkPackage commandPackage = new SendVideoChunkPackage {
        ChunkCount = (ushort)videoChunks.Count,
        VideoChunkData = videoChunk,
        ChunkIndex = chunkIndex,
        VideoHandle = videoID,
        ClipID = clipID
      };
      if(chunkIndex == 0) {
        BinarySerializer serializer = new BinarySerializer(512, Allocator.Persistent);
        contentBuffer.Serialize(serializer);
        commandPackage.ContentEventData = serializer.buffer;
      }

      if(m_UseSteamNetwork && !isReRequest) {
        NativeArray<byte> buffer = commandPackage.Serialize().buffer;
        byte[] dst = new byte[buffer.Length];
        ByteArrayConvertion.MoveToByteArray<byte>(ref buffer, ref dst);
        buffer.Dispose();
        m_SteamLobby.SendPackageToAll(dst);
        FoundFootagePlugin.Logger.LogError("Sent chunk! " + (++chunkIndex));
      } else if(CustomCommands<CustomCommandType>.SendPackage(commandPackage,
                  ReceiverGroup.Others))
        FoundFootagePlugin.Logger.LogError("Sent chunk! " + (++chunkIndex));
      else
        FoundFootagePlugin.Logger.LogError("Failed To Send chunk!");

      Thread.Sleep(500);
    }

    m_Sending = false;
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
    Debug.LogError(message);
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
    Debug.LogError("RPC_Success shitted");
    if(!PhotonNetwork.IsMasterClient) return;

    var recordings = RecordingsHandler.GetRecordings();
    foreach(var (videoID, recording) in recordings) {
      Debug.LogError($"Check {videoID}");
      if(GuidUtils.IsLocal(videoID.id)) continue;

      if(FoundFootagePlugin.Instance.Random.NextDouble() <= FoundFootagePlugin.Instance.PassUploadChance.Value) {
        PhotonGameLobbyHandlerPatch.UploadRecording(videoID, recording, "extract");
      } else {
        Debug.LogError($"Do not extracting");
      }
    }
  }
}

[HarmonyPatch(typeof(ArtifactSpawner))]
internal static class RoundArtifactSpawnerPatch {
  [HarmonyPostfix]
  [HarmonyPatch("Start")]
  internal static void Awake(ArtifactSpawner __instance) {
    Debug.LogError("Start shitted");

    if(!PhotonNetwork.IsMasterClient) {
      Debug.LogError("Not a master client");
      return;
    }

    if(FoundFootagePlugin.Instance.Random.NextDouble() <= FoundFootagePlugin.Instance.SpawnChance.Value) {
      FoundFootagePlugin.Logger.LogInfo("[RoundArtifactSpawnerPatch] Spawning found camera!");

      Vector3 pos = __instance.transform.position + Vector3.up * __instance.spawnAbovePatrolPoint;
      ItemInstanceData instance = new ItemInstanceData(Guid.NewGuid());
      VideoInfoEntry entry = new VideoInfoEntry();
      entry.videoID = new VideoHandle(GuidUtils.MakeLocal(Guid.NewGuid()));
      entry.maxTime = 0;
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

  [HarmonyPostfix]
  [HarmonyPatch("Start")]
  internal static void CreateArtifactSpawners(RoundArtifactSpawner __instance) {
    // Debug.LogError("CreateArtifactSpawners shitted");
    // for (int index = 0; index < 100; ++index)
    // {
    //   Debug.LogError($"CreateArtifactSpawners shit {index}");
    //   GameObject spawner = Object.Instantiate(__instance.artifactSpawnerPrefab, RoundArtifactSpawner.GetRandPointWithWeight(), Quaternion.identity);
    //   spawner.AddComponent<CameraSpawner>();
    //   Object.DestroyImmediate(spawner.GetComponent<ArtifactSpawner>());
    //   CameraSpawner component = spawner.GetComponent<CameraSpawner>();
    //   component.transform.parent = __instance.transform;
    //   component.gameObject.SetActive(true);
    // }
  }
}

// [HarmonyPatch(typeof(ArtifactSpawner))]
// internal static class ArtifactSpawnerPatch {
//   [HarmonyPostfix]
//   [HarmonyPatch("Update")]
//   internal static void Update(RoundArtifactSpawner __instance, ref float ___maxWaitForRest, ref int ___maxNrOfThrows) {
//     Debug.LogError("ArtifactSpawner.Update shitted");
//     ___maxWaitForRest = 0;
//     ___maxNrOfThrows = 1000;
//   }
// }

[HarmonyPatch(typeof(PhotonGameLobbyHandler))]
internal static class PhotonGameLobbyHandlerPatch {
  private static IEnumerator WaitThen(float t, Action a) {
    yield return new WaitForSecondsRealtime(t);
    a();
  }

  [HarmonyPostfix]
  [HarmonyPatch("ReturnToSurface")]
  internal static void ReturnToSurface(PhotonGameLobbyHandler __instance) {
    Debug.LogError("ReturnToSurface enter");
    if(Object.FindObjectsOfType<Player>().Where(pl => !pl.ai).All(player => player.data.dead)) {
      Debug.LogError("CheckForAllDead true");

      __instance.StartCoroutine(WaitThen(5f, () => {
        var recordings = RecordingsHandler.GetRecordings();
        // var camerasCurrentRecording = RecordingsHandler.GetCamerasCurrentRecording();
        //
        // foreach(var guid in camerasCurrentRecording.GetKeys()) {
        //   Debug.LogError($"Camera recording: {guid}");
        //   if(CameraHandler.TryGetCamera(guid, out VideoCamera camera)) {
        //     Debug.LogError($"Stop recording for recording camera {guid}");
        //     camera.StopRecording();
        //   }
        //   if(ItemInstanceDataHandler.TryGetInstanceData(guid, out ItemInstanceData data)) {
        //     Debug.LogError($"Stop recording for recording handler {guid}");
        //     RecordingsHandler.StopRecording(data);
        //   }
        // }

        foreach(var (videoID, recording) in recordings) {
          Debug.LogError($"Check {videoID}");
          if(GuidUtils.IsLocal(videoID.id)) continue;

          if(FoundFootagePlugin.Instance.Random.NextDouble() <= FoundFootagePlugin.Instance.DeathUploadChance.Value) {
            UploadRecording(videoID, recording, "death");
          } else {
            Debug.LogError($"Do not extracting");
          }
        }

        Debug.LogError("CheckForAllDead shitted");
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
          FoundFootagePlugin.Logger.LogError($"Failed to extract {videoID}, retrying...");
        }

        if(success) {
          var path = Path.Combine(recording.GetDirectory(), "fullRecording.webm");
          FoundFootagePlugin.Logger.LogError($"Extracted {videoID}: {path}");
          HttpUtils.UploadFile($"{FoundFootagePlugin.Instance.ServerUrl.Value}/videos", path,
            new Dictionary<string, string> {
              ["video_id"] = recording.videoHandle.id.ToString(),
              ["user_id"] = FoundFootagePlugin.Instance.UserId.Value,
              ["language"] = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName,
              ["reason"] = reason
            });
          FoundFootagePlugin.Logger.LogError($"Uploaded video!");
        } else {
          FoundFootagePlugin.Logger.LogError($"Failed to extract {videoID}");
        }
      } catch(Exception exception) {
        FoundFootagePlugin.Logger.LogError(exception);
      }
    }).Start();
  }
}
