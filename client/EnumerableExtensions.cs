using System;
using System.Collections.Generic;

namespace FoundFootage;

public static class EnumerableExtensions {
  public static IEnumerable<byte[]> SplitArray(byte[] source, int chunkSize) {
    if(source == null) throw new ArgumentNullException(nameof(source));
    if(chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));

    int currentIndex = 0;
    while(true) {
      if(currentIndex + chunkSize < source.Length) {
        // Extract the next chunk from the data
        yield return source[currentIndex..(currentIndex + chunkSize)];
      } else {
        // Extract the remaining data
        yield return source[currentIndex..];
        break;
      }

      currentIndex += chunkSize;
    }
  }
}
