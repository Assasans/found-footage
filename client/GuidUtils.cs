using System;
using System.Reflection;

namespace FoundFootage;

public static class GuidUtils {
  public static Guid MakeLocal(Guid guid) {
    var property = typeof(Guid).GetField("_a", BindingFlags.Instance | BindingFlags.NonPublic);
    object boxed = guid;
    property.SetValue(boxed, int.MaxValue);
    return (Guid)boxed;
  }

  public static bool IsLocal(Guid guid) {
    return (int)typeof(Guid)
      .GetField("_a", BindingFlags.Instance | BindingFlags.NonPublic)
      .GetValue(guid) == int.MaxValue;
  }
}
