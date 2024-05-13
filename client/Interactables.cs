using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Photon.Pun;
using UnityEngine;
using UnityEngine.Networking;

namespace FoundFootage;

public class LikeInteractable : Interactable {
  private CameraRecording? _recording;
  private bool _enabled = true;

  private void Start() {
    hoverText = "LIKE THE VIDEO";
  }

  public override bool IsValid(Player player) => _recording != null && _enabled;

  public override void Interact(Player player) {
    FoundFootagePlugin.Logger.LogInfo($"LIKE INTERACTED {_recording}");
    if(!FoundFootagePlugin.Instance.ClientToServerId.TryGetValue(_recording.videoHandle, out string? videoId)) {
      HelmetText.Instance.SetHelmetText("Cannot vote: outdated host", 3);
      return;
    }

    // Button is supposed to disable itself, but someone still spammed voting requests...
    if(FoundFootagePlugin.Instance.SentVotes.Contains(_recording.videoHandle)) {
      return;
    }

    new Thread(() => {
      _enabled = false;
      FoundFootagePlugin.Instance.SentVotes.Add(_recording.videoHandle);
      FoundFootagePlugin.Logger.LogInfo($"Sending vote for {videoId}...");

      try {
        VoteUtils.SendVote(this, videoId, VoteType.Like).GetAwaiter().GetResult();
        HelmetText.Instance.SetHelmetText("Thank you for voting", 3);
      } catch(WebException) {
        HelmetText.Instance.SetHelmetText("You have already voted", 3);
      }

      FoundFootagePlugin.Logger.LogInfo("Sent vote");
    }).Start();
  }

  public void SetRecording(CameraRecording recording) => _recording = recording;
}

public class DislikeInteractable : Interactable {
  private CameraRecording? _recording;
  private bool _enabled = true;

  private void Start() {
    hoverText = "DISLIKE THE VIDEO";
  }

  public override bool IsValid(Player player) => _recording != null && _enabled;

  public override void Interact(Player player) {
    FoundFootagePlugin.Logger.LogInfo($"DISLIKE INTERACTED {_recording}");
    if(!FoundFootagePlugin.Instance.ClientToServerId.TryGetValue(_recording.videoHandle, out string? videoId)) {
      HelmetText.Instance.SetHelmetText("Cannot vote: outdated host", 3);
      return;
    }

    // Button is supposed to disable itself, but someone still spammed voting requests...
    if(FoundFootagePlugin.Instance.SentVotes.Contains(_recording.videoHandle)) {
      return;
    }

    new Thread(() => {
      _enabled = false;
      FoundFootagePlugin.Instance.SentVotes.Add(_recording.videoHandle);
      FoundFootagePlugin.Logger.LogInfo($"Sending vote for {videoId}...");

      try {
        VoteUtils.SendVote(this, videoId, VoteType.Dislike).GetAwaiter().GetResult();
        HelmetText.Instance.SetHelmetText("Thank you for voting", 3);
      } catch(WebException) {
        HelmetText.Instance.SetHelmetText("You have already voted", 3);
      }

      FoundFootagePlugin.Logger.LogInfo("Sent vote");
    }).Start();
    HelmetText.Instance.SetHelmetText("Thank you for voting", 3);
  }

  public void SetRecording(CameraRecording recording) => _recording = recording;
}

public enum VoteType {
  Like = 1,
  Dislike = 2
}

public static class VoteUtils {
  private static IEnumerator SendVote_Unity(
    string videoId,
    VoteType type,
    Action<Result<string, Exception>> callback
  ) {
    var formData = new List<IMultipartFormSection>();
    formData.Add(new MultipartFormDataSection("video_id", videoId));
    formData.Add(new MultipartFormDataSection("user_id", FoundFootagePlugin.Instance.UserId.Value));
    formData.Add(new MultipartFormDataSection("lobby_id", PhotonNetwork.CurrentRoom.Name));
    formData.Add(new MultipartFormDataSection("vote_type", type switch {
      VoteType.Like => "like",
      VoteType.Dislike => "dislike",
      _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    }));

    byte[] boundary = UnityWebRequest.GenerateBoundary();
    using UnityWebRequest request = UnityWebRequest.Post(
      $"{FoundFootagePlugin.Instance.ServerUrl.Value}/video/vote?local={PluginInfo.PLUGIN_VERSION}",
      formData,
      boundary
    );

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

  public static async Task SendVote(MonoBehaviour target, string videoId, VoteType type) {
    try {
      var response = await UnityShit.CoroutineToTask<string>(target, callback => SendVote_Unity(videoId, type, callback));
      FoundFootagePlugin.Logger.LogInfo($"Vote sent successfully: {response}");
    } catch(Exception exception) {
      FoundFootagePlugin.Logger.LogError($"An error occurred while sending vote: {exception}");
      throw;
    }
  }
}
