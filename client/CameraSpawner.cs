using System;
using DefaultNamespace.Artifacts;
using Photon.Pun;
using UnityEngine;

namespace FoundFootage;

public class CameraSpawner : MonoBehaviour {
  public Pickup spawnedArtifact;
  public float spawnAbovePatrolPoint = 1f;
  public Vector2 minMaxThrowForce = new Vector2(5f, 30f);
  public float maxWaitForRest = 5f;
  private readonly int maxNrOfThrows = 10;
  private int nrOfThrows;
  private float timeSinceThrow;
  public bool debugDontMove;
  public Pickup completedSpawn;

  private void Start() => timeSinceThrow = float.MaxValue;

  private void Update() {
    if(!PhotonNetwork.IsMasterClient)
      return;
    timeSinceThrow += Time.deltaTime;
    if(nrOfThrows > maxNrOfThrows) {
      Debug.LogWarning("Max nr of throws reached DELETING ITEM AND SPAWNER");
      PhotonNetwork.Destroy(spawnedArtifact.gameObject);
      DestroyImmediate(gameObject);
    } else if(timeSinceThrow > (double)maxWaitForRest) {
      Vector3 position1 = transform.position;
      if(!debugDontMove)
        transform.position = RoundArtifactSpawner.GetRandPointWithWeight();
      Vector3 position2 = transform.position;
      Color red = Color.red;
      Debug.DrawLine(position1, position2, red, 10f);
      Vector3 pos = transform.position + Vector3.up * spawnAbovePatrolPoint;
      if(spawnedArtifact == null) {
        FoundFootagePlugin.Logger.LogInfo("[CameraSpawner] Spawning extra camera!");

        ItemInstanceData instance = new ItemInstanceData(Guid.NewGuid());
        VideoInfoEntry entry = new VideoInfoEntry();
        entry.videoID = new VideoHandle(GuidUtils.MakeLocal(Guid.NewGuid()));
        entry.maxTime = 0;
        entry.timeLeft = 0;
        entry.SetDirty();
        instance.AddDataEntry(entry);
        FoundFootagePlugin.Instance.FakeVideos.Add(entry.videoID);
        FoundFootagePlugin.Logger.LogInfo($"[CameraSpawner] added entry {entry.videoID.id}");
        spawnedArtifact = PickupHandler.CreatePickup(1, instance, pos,
          UnityEngine.Random.rotation);
        FoundFootagePlugin.Logger.LogInfo("[CameraSpawner] Spawned extra camera!");
      }

      timeSinceThrow = 0.0f;
      ++nrOfThrows;
      spawnedArtifact.Rigidbody.position = pos;
      spawnedArtifact.Rigidbody.AddForce(UnityEngine.Random.onUnitSphere * minMaxThrowForce.x, ForceMode.Impulse);
      spawnedArtifact.Rigidbody.AddTorque(UnityEngine.Random.onUnitSphere * (minMaxThrowForce.x * 0.2f));
    } else {
      if(!(spawnedArtifact != null) || !spawnedArtifact.Rigidbody.IsSleeping())
        return;
      spawnedArtifact.GetComponentInChildren<ISpawnedByArtifactSpawner>()?.OnFinishSpawning();
      completedSpawn = spawnedArtifact;
      spawnedArtifact = null;
      gameObject.SetActive(false);
    }
  }
}
