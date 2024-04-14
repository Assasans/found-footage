using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FoundFootage;

internal static class MessageBoxUtils {
  [DllImport("user32.dll", CharSet = CharSet.Auto)]
  private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

  [DllImport("user32.dll")]
  private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

  [DllImport("user32.dll")]
  private static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

  [DllImport("user32.dll")]
  private static extern bool IsWindowVisible(IntPtr hWnd);

  [DllImport("user32.dll")]
  private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, WindowCallback lParam);

  private delegate void WindowCallback(IntPtr hWnd);

  private delegate bool EnumWindowsProc(IntPtr hWnd, WindowCallback callback);

  private static bool EnumWindowsCallback(IntPtr handle, WindowCallback callback) {
    GetWindowThreadProcessId(handle, out var pid);
    if(pid == System.Diagnostics.Process.GetCurrentProcess().Id) {
      StringBuilder title = new StringBuilder(256);
      if(IsWindowVisible(handle) && GetWindowText(handle, title, title.Capacity) > 0) {
        callback(handle);
        return false;
      }
    }

    return true;
  }

  internal static void Show(string title, string content, uint type) {
    bool success = false;
    EnumWindows(EnumWindowsCallback, handle => {
      success = true;
      MessageBox(handle, content, title, type);
    });

    if(!success) MessageBox(IntPtr.Zero, content, title, type);
  }
}
