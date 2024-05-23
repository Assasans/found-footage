using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FoundFootage;

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
