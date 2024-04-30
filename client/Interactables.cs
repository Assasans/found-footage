using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Photon.Pun;

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
        VoteUtils.SendVote(videoId, VoteType.Like).GetAwaiter().GetResult();
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
        VoteUtils.SendVote(videoId, VoteType.Dislike).GetAwaiter().GetResult();
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
  public static async Task SendVote(string videoId, VoteType type) {
    string boundary = "---------------------------" + DateTime.Now.Ticks.ToString("x");
    HttpWebRequest request =
      (HttpWebRequest)WebRequest.Create($"{FoundFootagePlugin.Instance.ServerUrl.Value}/video/vote");
    request.Method = "POST";
    request.ContentType = "multipart/form-data; boundary=" + boundary;

    using(Stream requestStream = request.GetRequestStream()) {
      // Write boundary and file header
      HttpUtils.WriteBoundary(requestStream, boundary);
      HttpUtils.WriteFormValue(requestStream, "video_id", videoId, boundary);
      HttpUtils.WriteFormValue(requestStream, "user_id", FoundFootagePlugin.Instance.UserId.Value, boundary);
      HttpUtils.WriteFormValue(requestStream, "lobby_id", PhotonNetwork.CurrentRoom.Name, boundary);
      HttpUtils.WriteFormValue(requestStream, "vote_type", type switch {
        VoteType.Like => "like",
        VoteType.Dislike => "dislike",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
      }, boundary + "--");
    }

    try {
      // Fuck FCL
      using HttpWebResponse response = (HttpWebResponse)request.GetResponse();

      await using Stream responseStream = response.GetResponseStream();
      using StreamReader reader = new StreamReader(responseStream);
      string responseText = await reader.ReadToEndAsync();

      FoundFootagePlugin.Logger.LogInfo($"Vote sent successfully: {responseText}");
    } catch(WebException exception) {
      FoundFootagePlugin.Logger.LogError($"An error occurred while sending vote: {exception}");
      throw;
    }
  }
}
