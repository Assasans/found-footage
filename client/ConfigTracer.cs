using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FoundFootage;

public class ConfigTracer {
  private IEnumerator TraceConfig_Unity(ConfigTrace trace, Action<Result<int, Exception>> callback) {
    FoundFootagePlugin.Logger.LogInfo($"Trace: {JsonUtility.ToJson(trace)}");
    using UnityWebRequest request = UnityWebRequest.Post(
      $"{FoundFootagePlugin.Instance.ServerUrl.Value}/trace-config?local={PluginInfo.PLUGIN_VERSION}",
      JsonUtility.ToJson(trace),
      "application/json"
    );
    yield return request.SendWebRequest();
    if(request.result != UnityWebRequest.Result.Success) {
      // What the fuck should I return?
      callback(Result<int, Exception>.NewError(new Exception(
        $"request.GetError={request.GetError()} request.error={request.error} downloadHandler.GetErrorMsg={request.downloadHandler.GetErrorMsg()} downloadHandler.error={request.downloadHandler.error}"
      )));
    } else {
      callback(Result<int, Exception>.NewOk(0));
    }
  }

  public async Task TraceConfig(MonoBehaviour target, ConfigTrace trace) {
    try {
      await UnityShit.CoroutineToTask<int>(target, callback => TraceConfig_Unity(trace, callback));
    } catch(Exception exception) {
      FoundFootagePlugin.Logger.LogError($"An error occurred while tracing config: {exception.Message}");
    }
  }

  public bool IsCompatibleWith(string compatible, string actual) {
    string[] components = compatible.Split('(');
    if(components[0].Trim() == actual) return true;
    if(components[1].Contains($"{actual}-compatible")) return true;
    return false;
  }
}

public class ConfigTrace {
  [SerializeField] public string secretUserId;
  // Unity :) cannot serialize complex objects
  [SerializeField] public string values;
}
