using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FoundFootage.Patches;
using HarmonyLib;
using MyceliumNetworking;
using MyceliumPluginInfo = MyceliumNetworking.MyPluginInfo;
using Photon.Pun;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Assertions;
using Zorro.Core.Serizalization;
using Random = System.Random;

namespace FoundFootage;

public enum FoundVideoSearchParam {
  LANGUAGE,
  DAY,
  PLAYERS_COUNT
}

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
  internal ConfigEntry<bool>? ConfigTraceEnabled { get; private set; }
  internal ConfigEntry<int>? ConfigVersion { get; private set; }

  internal ConfigEntry<bool>? VotingEnabled { get; private set; }
  internal ConfigEntry<bool>? SendPositionEnabled { get; private set; }
  internal ConfigEntry<bool>? SendContentBufferEnabled { get; private set; }
  internal ConfigEntry<float>? FoundVideoScoreMultiplier { get; private set; }
  internal ConfigEntry<string>? FoundVideoSearchParams { get; private set; }

  internal IEnumerable<FoundVideoSearchParam> FoundVideoSearchParamsParsed => FoundVideoSearchParams.Value
    .TrimStart('[')
    .TrimEnd(']')
    .Split(",")
    .Select(name => name.Trim())
    .Select(Enum.Parse<FoundVideoSearchParam>);

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
    ConfigTraceEnabled = PersistentConfig.Bind("Internal", "ConfigTraceEnabled", true,
      "Controls whether the mod's config is sent to the server when the game starts.\nThis is used to improve defaults and see how experimental features are used.");
    ConfigVersion = Config.Bind("Internal", "ConfigVersion", 1, "");

    VotingEnabled = Config.Bind("Voting", "VotingEnabled", true, "Enable voting after watching another team's video.");
    SendPositionEnabled = Config.Bind("Voting", "SendPositionEnabled", true,
      "Send camera position on death to spawn camera at same position for other teams.");
    SendContentBufferEnabled = Config.Bind("Voting", "SendContentBufferEnabled", true,
      "Send recording content buffer (scoring data) to allow your videos to give views when viewed by other teams.");
    FoundVideoScoreMultiplier = Config.Bind("Voting", "FoundVideoScoreMultiplier", 0.5f,
      "Controls how much of the original video score you receive. (1 - original score, 0 to disable views for fake videos).");
    FoundVideoSearchParams = Config.Bind(
      "Voting",
      "FoundVideoSearchParams",
      $"[{string.Join(", ", new List<string> {
        nameof(FoundVideoSearchParam.LANGUAGE),
        nameof(FoundVideoSearchParam.DAY),
      })}]",
      "Controls which criteria are used to retrieve found videos.\nServer is not required to respect this setting and may return arbitrary videos.\nAvailable values: [LANGUAGE, DAY, PLAYERS_COUNT]\nSet to [] to get completely random videos."
    );

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

    MigrateConfig();
    ShowWarningIfNeeded();
    CheckVersion();
    TraceConfigIfNeeded();

    Dispatcher = gameObject.AddComponent<MainThreadDispatcher>();
  }

  private void MigrateConfig() {
    if(ConfigVersion.Value < 2) {
      ConfigVersion.Value = 2;
      if(PassUploadChance.Value.AlmostEquals(1f, 0.01f)) {
        PassUploadChance.Value = (float)PassUploadChance.DefaultValue;
        Logger.LogInfo($"Set PassUploadChance to {PassUploadChance.Value}");
      }

      Logger.LogInfo($"Migrated to config version {ConfigVersion.Value}");
    }
  }

  private void ShowWarningIfNeeded() {
    if(WarningShown.Value) return;
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

  private void CheckVersion() {
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
  }

  private void TraceConfigIfNeeded() {
    if(!ConfigTraceEnabled.Value) return;
    Task.Run(async () => {
      try {
        ConfigTracer tracer = new ConfigTracer();
        var trace = new ConfigTrace {
          secretUserId = SecretUserId.Value,
          values = string.Join("\n", new Dictionary<string, object> {
            ["SpawnChance"] = SpawnChance.Value,
            ["DeathUploadChance"] = DeathUploadChance.Value,
            ["PassUploadChance"] = PassUploadChance.Value,
            ["DeathVideoChance"] = DeathVideoChance.Value,
            ["VotingEnabled"] = VotingEnabled.Value,
            ["SendPositionEnabled"] = SendPositionEnabled.Value,
            ["SendContentBufferEnabled"] = SendContentBufferEnabled.Value,
            ["FoundVideoScoreMultiplier"] = FoundVideoScoreMultiplier.Value,
            ["FoundVideoSearchParams"] = FoundVideoSearchParams.Value
          }.Select(entry => $"{entry.Key}={entry.Value}"))
        };
        await tracer.TraceConfig(this, trace);
      } catch(Exception exception) {
        Logger.LogError(exception);
      }
    });
  }

  private ContentEventFrame CreateContentEventForgiving(BinaryDeserializer deserializer) {
    var frame = new ContentEventFrame();
    frame.seenAmount = deserializer.ReadFloat();
    frame.time = deserializer.ReadFloat();
    frame.contentEvent = ContentEventIDMapper.GetContentEvent(deserializer.ReadUShort());
    int position = deserializer.position;
    frame.contentEvent.Deserialize(deserializer);
    int num1 = deserializer.position - position;
    int num2 = deserializer.ReadInt();
    if(num1 != num2) {
      Logger.LogError(
        $"Size mismatch on content event: {frame.contentEvent.GetType().Name}. Expected: {num2}, Read: {num1}"
      );
    }

    return frame;
  }

  private ContentBuffer.BufferedContent CreateBufferedContentForgiving(BinaryDeserializer deserializer) {
    return new ContentBuffer.BufferedContent {
      score = deserializer.ReadFloat(),
      frame = CreateContentEventForgiving(deserializer)
    };
  }

  private ContentBuffer? CreateContentBufferForgiving(BinaryDeserializer deserializer) {
    int num = deserializer.ReadInt();
    var bufferedContentList = new List<ContentBuffer.BufferedContent>();
    for(int index = 0; index < num; ++index) {
      try {
        bufferedContentList.Add(CreateBufferedContentForgiving(deserializer));
      } catch(Exception exception) {
        Logger.LogError($"Failed to read BufferedContent {index}: {exception}");
        return null;
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
        if(buffer == null) return;

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
  [SerializeField] public int day;
  [SerializeField] public int playerCount;
  [SerializeField] public string? reason;
  [SerializeField] public string? language;
}

public class GetSignedVideoResponse {
  [SerializeField] public string url;
  [SerializeField] public string videoId;
  [SerializeField] public string? position;
  [SerializeField] public string? contentBuffer;
}
