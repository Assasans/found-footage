using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FoundFootage;

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
