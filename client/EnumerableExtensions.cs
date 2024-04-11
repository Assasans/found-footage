using System;
using System.Collections.Generic;

namespace FoundFootage;

public static class EnumerableExtensions {
  public static IEnumerable<T[]> SplitArray<T>(IEnumerable<T> source, int size) {
    if(null == source) throw new ArgumentNullException(nameof(source));
    if(size <= 0) throw new ArgumentOutOfRangeException(nameof(size));

    List<T> list = new List<T>(size);
    foreach(T item in source) {
      list.Add(item);

      if(list.Count >= size) {
        yield return list.ToArray();

        list.Clear();
      }
    }

    // Do we have last incomplete chunk?
    if(list.Count > 0) yield return list.ToArray();
  }
}
