using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace FoundFootage;

public class MainThreadDispatcher : MonoBehaviour {
  #region Singleton pattern (optional)

  private static MainThreadDispatcher _instance;
  public static MainThreadDispatcher Instance => _instance;

  private void Awake() {
    if(_instance && _instance != this) {
      Destroy(gameObject);
      return;
    }

    _instance = this;
    DontDestroyOnLoad(gameObject);
  }

  #endregion Singleton

  #region Dispatcher

  // You use a thread-safe collection first-in first-out so you can pass on callbacks between the threads
  private readonly ConcurrentQueue<Action> mainThreadActions = new();

  // This now can be called from any thread/task etc
  // => dispatched action will be executed in the next Unity Update call
  public void Dispatch(Action action) {
    mainThreadActions.Enqueue(action);
  }

  // In the Unity main thread Update call routine you work off whatever has been enqueued since the last frame
  private void Update() {
    while(mainThreadActions.TryDequeue(out var action)) {
      action?.Invoke();
    }
  }

  #endregion Dispatcher
}
