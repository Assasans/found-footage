using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.PropertyVariants;
using UnityEngine.UI;

namespace FoundFootage.Patches;

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
