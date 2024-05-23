using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

namespace FoundFootage;

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
