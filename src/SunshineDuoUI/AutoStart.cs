using Microsoft.Win32;

namespace SunshineDuoTray;

// 登录自启：HKCU Run 键写入当前 exe 路径
internal static class AutoStart
{
    public static void Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (on)
                key.SetValue("SunshineDuoTray", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("SunshineDuoTray", false);
        }
        catch { }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            var v = key?.GetValue("SunshineDuoTray") as string;
            return v != null && v.Contains("SunshineDuoTray.exe");
        }
        catch { return false; }
    }
}
