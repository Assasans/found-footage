using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
using MyceliumPluginInfo = MyceliumNetworking.MyPluginInfo;
using Photon.Pun;
using TMPro;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Localization.PropertyVariants;
using UnityEngine.Networking;
using UnityEngine.UI;
using Zorro.Core.Serizalization;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace FoundFootage;

[ContentWarningPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_VERSION, false)]
[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
[BepInDependency(MyceliumPluginInfo.PLUGIN_GUID, MyceliumPluginInfo.PLUGIN_VERSION)]
public class FoundFootagePlugin : BaseUnityPlugin {
  public const uint ModId = 0x20d82112;

  public static FoundFootagePlugin Instance { get; private set; }
  internal static ManualLogSource Logger { get; private set; }

  internal Random Random { get; private set; }
  internal List<VideoHandle> FakeVideos { get; private set; }
  internal List<VideoHandle> ProcessedFakeVideos { get; private set; }
  internal List<VideoHandle> SentVideos { get; private set; }
  internal List<VideoHandle> SentVotes { get; private set; }
  internal Dictionary<VideoHandle, string> ClientToServerId { get; private set; }
  internal Dictionary<VideoHandle, ContentBuffer> FakeContentBuffers { get; private set; }
  internal MainThreadDispatcher Dispatcher { get; private set; }

  internal ConfigFile PersistentConfig { get; private set; }

  internal ConfigEntry<string>? ServerUrl { get; private set; }
  internal ConfigEntry<string>? UserId { get; private set; }
  internal ConfigEntry<string>? SecretUserId { get; private set; }
  internal ConfigEntry<bool>? WarningShown { get; private set; }
  internal ConfigEntry<int>? ConfigVersion { get; private set; }

  internal ConfigEntry<bool>? VotingEnabled { get; private set; }
  internal ConfigEntry<bool>? SendPositionEnabled { get; private set; }
  internal ConfigEntry<bool>? SendContentBufferEnabled { get; private set; }
  internal ConfigEntry<float>? FoundVideoScoreMultiplier { get; private set; }

  internal ConfigEntry<float>? SpawnChance { get; private set; }
  internal ConfigEntry<float>? DeathUploadChance { get; private set; }
  internal ConfigEntry<float>? PassUploadChance { get; private set; }
  internal ConfigEntry<float>? DeathVideoChance { get; private set; }

  private void Awake() {
    Instance = this;
    Logger = base.Logger;

    Random = new Random();
    FakeVideos = new List<VideoHandle>();
    ProcessedFakeVideos = new List<VideoHandle>();
    SentVideos = new List<VideoHandle>();
    SentVotes = new List<VideoHandle>();
    ClientToServerId = new Dictionary<VideoHandle, string>();
    FakeContentBuffers = new Dictionary<VideoHandle, ContentBuffer>();

    BepInPlugin metadata = MetadataHelper.GetMetadata(this);
    Assert.IsNotNull(metadata); // Unreachable, checked in base class constructor
    var persistentConfigDirectory = Path.Combine(Paths.BepInExRootPath, "persistent-config");

    // Migrate old config, r2modman doesn't care about privacy
    var oldPersistentConfigPath = Utility.CombinePaths(persistentConfigDirectory, $"{metadata.GUID}.cfg");
    var persistentConfigPath = Utility.CombinePaths(persistentConfigDirectory, $"{metadata.GUID}.privatecfg");
    if(File.Exists(oldPersistentConfigPath)) {
      File.Move(oldPersistentConfigPath, persistentConfigPath);
      Logger.LogInfo("Migrated persistent config to .privatecfg");
    }

    PersistentConfig = new ConfigFile(
      persistentConfigPath,
      false,
      metadata
    );

    ServerUrl = PersistentConfig.Bind("Internal", "ServerUrl", "https://foundfootage-server.assasans.dev",
      "Base URL of the server hosting the videos.\n### DATA REQUEST OR REMOVAL ###\nIf you want to request your data or have it removed:\nsend an email to the address available on the \"/info\" endpoint (e.g. https://server.local/info).\nNote that your email must include your Secret User ID (see below).");
    UserId = PersistentConfig.Bind("Internal", "UserId", "",
      "Random identifier associated with uploaded recordings and votes.");
    SecretUserId = PersistentConfig.Bind("Internal", "SecretUserId", "",
      "Random identifier associated with Public User ID. Can be used to request or remove your data.");
    if(UserId.Value == (string)UserId.DefaultValue) {
      UserId.Value = Guid.NewGuid().ToString();
      PersistentConfig.Save();
      Logger.LogInfo($"Generated user UUID: {UserId.Value}");
    }

    if(SecretUserId.Value == (string)SecretUserId.DefaultValue) {
      SecretUserId.Value = Guid.NewGuid().ToString();
      PersistentConfig.Save();
      Logger.LogInfo($"Generated secret user UUID: {new string('*', UserId.Value.Length)}");
    }

    WarningShown = PersistentConfig.Bind("Internal", "WarningShown", false, "");
    ConfigVersion = Config.Bind("Internal", "ConfigVersion", 1, "");

    VotingEnabled = Config.Bind("Voting", "VotingEnabled", true, "Enable voting after watching another team's video.");
    SendPositionEnabled = Config.Bind("Voting", "SendPositionEnabled", true,
      "Send camera position on death to spawn camera at same position for other teams.");
    SendContentBufferEnabled = Config.Bind("Voting", "SendContentBufferEnabled", true,
      "Send recording content buffer (scoring data) to allow your videos to give views when viewed by other teams.");
    FoundVideoScoreMultiplier = Config.Bind("Voting", "FoundVideoScoreMultiplier", 0.5f,
      "Controls how much of the original video score you receive. (1 - original score, 0 to disable views for fake videos).");

    SpawnChance = Config.Bind("Chances", "SpawnChance", 0.3f,
      "Chance that another team's camera will be spawned (0 to disable).");
    DeathUploadChance = Config.Bind("Chances", "DeathUploadChance", 1f,
      "Chance that the camera video will be uploaded when all team members die (1 - always, 0 to disable).");
    PassUploadChance = Config.Bind("Chances", "PassUploadChance", 0.25f,
      "Chance that the camera video will be uploaded when the team returns to surface (1 - always, 0 to disable).");
    DeathVideoChance = Config.Bind("Chances", "DeathVideoChance", 0.75f,
      "Chance that the found camera will contain video of a dead team, instead of a survived one (1 - always, 0 - never).");

    // Plugin startup logic
    Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
    Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), PluginInfo.PLUGIN_GUID);
    Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} patched!");
    Logger.LogInfo($"Server URL: {Instance.ServerUrl?.Value}");

    MyceliumNetwork.RegisterNetworkObject(this, ModId);

    if(ConfigVersion.Value < 2) {
      ConfigVersion.Value = 2;
      if(PassUploadChance.Value.AlmostEquals(1f, 0.01f)) {
        PassUploadChance.Value = (float)PassUploadChance.DefaultValue;
        Logger.LogInfo($"Set PassUploadChance to {PassUploadChance.Value}");
      }

      Logger.LogInfo($"Migrated to config version {ConfigVersion.Value}");
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
        string remote = await checker.GetVersion(this);
        if(remote == VersionChecker.IncompatibleVersion) {
          // Try to change to new URL
          if(ServerUrl.Value == "https://foundfootage-server.assasans.dev") {
            ServerUrl.Value = (string)ServerUrl.DefaultValue;
            Logger.LogInfo($"Updated server URL to {ServerUrl.Value}");

            remote = await checker.GetVersion(this);
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

    Dispatcher = gameObject.AddComponent<MainThreadDispatcher>();
  }

  private ContentBuffer CreateContentBufferForgiving(BinaryDeserializer deserializer) {
    int num = deserializer.ReadInt();
    List<ContentBuffer.BufferedContent> bufferedContentList = new List<ContentBuffer.BufferedContent>();
    for(int index = 0; index < num; ++index) {
      try {
        ContentBuffer.BufferedContent bufferedContent = new ContentBuffer.BufferedContent();
        bufferedContent.Deserialize(deserializer);
        bufferedContentList.Add(bufferedContent);
      } catch(Exception exception) {
        Logger.LogError($"Failed to read BufferedContent {index}: {exception}");
      }
    }

    return new ContentBuffer {
      buffer = bufferedContentList
    };
  }

  private void CreateFakeContentBuffer(VideoHandle handle, GetSignedVideoResponse response) {
    try {
      Logger.LogInfo("CreateFakeContentBuffer!");
      if(response.contentBuffer != null) {
        Logger.LogInfo("Saving remote content buffer...");
        Logger.LogInfo($"{response.contentBuffer}");
        var bufferBytes = Convert.FromBase64String(response.contentBuffer);
        Logger.LogInfo($"{bufferBytes.Length} bytes");
        using BinaryDeserializer deserializer = new BinaryDeserializer(bufferBytes, Allocator.Temp);
        Logger.LogInfo($"CreateContentBufferForgiving(deserializer)");
        var buffer = CreateContentBufferForgiving(deserializer);

        Logger.LogInfo($"Multiply score by {FoundVideoScoreMultiplier.Value}");
        foreach(var bufferedContent in buffer.buffer) {
          bufferedContent.score *= FoundVideoScoreMultiplier.Value;
        }

        FakeContentBuffers.Add(handle, buffer);
      }
    } catch(Exception exception) {
      Logger.LogError($"CreateFakeContentBuffer: {exception}");
    }
  }

  [CustomRPC]
  public void CreateFakeVideo(string guidString, string responseJson) {
    Logger.LogInfo("CreateFakeVideo!");
    VideoHandle handle = new VideoHandle(Guid.Parse(guidString));

    var response = JsonUtility.FromJson<GetSignedVideoResponse>(responseJson);
    ClientToServerId.Add(handle, response.videoId);

    if(!FoundVideoScoreMultiplier.Value.AlmostEquals(0f, 0.01f)) {
      Dispatcher.Dispatch(() => CreateFakeContentBuffer(handle, response));
    } else {
      Logger.LogInfo("Remote content buffer is disabled in config");
    }

    new Thread(() => {
      Logger.LogInfo($"Downloading video {guidString} -> {response.url}");
      byte[] content = VideoCameraPatch.DownloadFakeVideo(this, response.url).GetAwaiter().GetResult();

      Logger.LogInfo($"VideoCameraPatch.CreateFakeVideo");
      VideoCameraPatch.CreateFakeVideo(handle, content);
    }).Start();
  }
}

public class GetSignedVideoRequest {
  [SerializeField] public int? day;
  [SerializeField] public int? playerCount;
  [SerializeField] public string? reason;
  [SerializeField] public string? language;
}

public class GetSignedVideoResponse {
  [SerializeField] public string url;
  [SerializeField] public string videoId;
  [SerializeField] public string? position;
  [SerializeField] public string? contentBuffer;
}

public static class HttpUtils {
  private static IEnumerator UploadVideo_Unity(
    string filePath,
    Dictionary<string, string> properties,
    Action<Result<string, Exception>> callback
  ) {
    FoundFootagePlugin.Logger.LogInfo($"UploadVideo_Unity start");

    FoundFootagePlugin.Logger.LogInfo($"UploadVideo_Unity ReadAllBytes");
    var bytes = File.ReadAllBytes(filePath);

    FoundFootagePlugin.Logger.LogInfo($"UploadVideo_Unity IMultipartFormSection");
    var formData = new List<IMultipartFormSection>();
    formData.Add(new MultipartFormFileSection("file", bytes, "fullRecording.mp4", "application/octet-stream"));
    FoundFootagePlugin.Logger.LogInfo($"UploadVideo_Unity MultipartFormFileSection done");
    foreach(var (key, value) in properties) {
      var realValue = value;
      if(value == "") realValue = "null";

      FoundFootagePlugin.Logger.LogInfo($"UploadVideo_Unity MultipartFormDataSection {key}: {realValue}");
      formData.Add(new MultipartFormDataSection(key, realValue));
    }

    FoundFootagePlugin.Logger.LogInfo($"UploadVideo_Unity GenerateBoundary");
    byte[] boundary = UnityWebRequest.GenerateBoundary();
    FoundFootagePlugin.Logger.LogInfo($"UploadVideo_Unity SerializeFormSections");
    byte[] formSections = UnityWebRequest.SerializeFormSections(formData, boundary);

    FoundFootagePlugin.Logger.LogInfo($"UploadVideo_Unity Put");
    using UnityWebRequest request = UnityWebRequest.Put(
      $"{FoundFootagePlugin.Instance.ServerUrl.Value}/videos?local={PluginInfo.PLUGIN_VERSION}",
      formSections
    );
    request.SetRequestHeader("Content-Type", $"multipart/form-data; boundary=\"{Encoding.UTF8.GetString(boundary)}\"");

    FoundFootagePlugin.Logger.LogInfo($"UploadVideo_Unity waiting for response...");
    yield return request.SendWebRequest();
    FoundFootagePlugin.Logger.LogInfo($"UploadVideo_Unity got response");

    if(request.result != UnityWebRequest.Result.Success) {
      // What the fuck should I return?
      callback(Result<string, Exception>.NewError(new Exception(
        $"request.GetError={request.GetError()} request.error={request.error} downloadHandler.GetErrorMsg={request.downloadHandler.GetErrorMsg()} downloadHandler.error={request.downloadHandler.error}"
      )));
    } else {
      callback(Result<string, Exception>.NewOk(request.downloadHandler.text));
    }
  }

  public static async Task UploadVideo(MonoBehaviour target, string filePath, Dictionary<string, string> properties) {
    try {
      FoundFootagePlugin.Logger.LogInfo($"UploadVideo start");
      var response =
        await UnityShit.CoroutineToTask<string>(target, callback => UploadVideo_Unity(filePath, properties, callback));
      FoundFootagePlugin.Logger.LogInfo($"Upload success: {response}");
    } catch(Exception exception) {
      FoundFootagePlugin.Logger.LogError($"An error occurred while uploading video: {exception}");
      throw;
    }
  }
}

[HarmonyPatch(typeof(SurfaceNetworkHandler))]
internal static class SurfaceNetworkHandlerPatch {
  [HarmonyPostfix]
  [HarmonyPatch("InitSurface")]
  internal static void SpawnOnStartRun() {
    TimeOfDayHandler.SetTimeOfDay(TimeOfDay.Evening);
    SpawnExtraCamera();
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
      var requestParams = new GetSignedVideoRequest {
        day = SurfaceNetworkHandler.RoomStats.CurrentDay,
        playerCount = Object.FindObjectsOfType<Player>().Count(pl => !pl.ai),
        reason = new Random().NextDouble() < FoundFootagePlugin.Instance.DeathVideoChance.Value ? "death" : "extract",
        language = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName
      };
      new Thread(() => {
        GetSignedVideoResponse response = DownloadFakeVideoSignedUrl(FoundFootagePlugin.Instance, requestParams)
          .GetAwaiter().GetResult();
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
      }).Start();

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

  private static IEnumerator DownloadFakeVideo_Unity(string signedUrl, Action<Result<byte[], Exception>> callback) {
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

    if(!recordings.ContainsKey(handle)) {
      FoundFootagePlugin.Logger.LogInfo("m_recordings.Add()");
      recordings.Add(handle, recording);
    }

    FoundFootagePlugin.Logger.LogInfo("InitCamera shitted");
  }
}

public class Result<T, E> {
  public T? Ok { get; set; }
  public E? Error { get; set; }

  private Result(T? ok, E? error) {
    Ok = ok;
    Error = error;
  }

  public static Result<T, E> NewOk(T value) => new(value, default);
  public static Result<T, E> NewError(E value) => new(default, value);
}

public static class UnityShit {
  // Unity SUCKS, I do not want to use fucking coroutine API, it is not C# 4 anymore
  public static Task<T> CoroutineToTask<T>(
    MonoBehaviour target,
    Func<Action<Result<T, Exception>>, IEnumerator> block
  ) {
    var source = new TaskCompletionSource<T>();
    target.StartCoroutine(block(result => {
      if(result.Ok != null) source.SetResult(result.Ok);
      else if(result.Error != null) source.SetException(result.Error);
    }));

    return source.Task;
  }
}

public class VersionChecker {
  public static readonly string IncompatibleVersion = "0.0.0 (incompatible)";

  private IEnumerator GetVersion_Unity(Action<Result<string, Exception>> callback) {
    using UnityWebRequest request =
      UnityWebRequest.Get($"{FoundFootagePlugin.Instance.ServerUrl.Value}/version?local={PluginInfo.PLUGIN_VERSION}");
    yield return request.SendWebRequest();
    if(request.result != UnityWebRequest.Result.Success) {
      // What the fuck should I return?
      callback(Result<string, Exception>.NewError(new Exception(
        $"request.GetError={request.GetError()} request.error={request.error} downloadHandler.GetErrorMsg={request.downloadHandler.GetErrorMsg()} downloadHandler.error={request.downloadHandler.error}"
      )));
    } else {
      callback(Result<string, Exception>.NewOk(request.downloadHandler.text));
    }
  }

  public async Task<string> GetVersion(MonoBehaviour target) {
    try {
      return await UnityShit.CoroutineToTask<string>(target, GetVersion_Unity);
    } catch(Exception exception) {
      FoundFootagePlugin.Logger.LogError($"An error occurred while fetching version: {exception.Message}");
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
