using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace MemeManager;

/// <summary>
/// 开机自启管理：通过 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 实现。
/// 键值存在且指向当前 exe（含 --hidden 参数）即视为已开启；否则为关闭。
/// 使用 HKCU 无需管理员权限，且只对当前用户生效。
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "MemeManager";

    // 自启时静默后台：直接隐藏主窗口、只留托盘，不闪界面
    public const string LaunchArgs = "--hidden";

    private static string CurrentExePath =>
        Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    private static string DesiredValue =>
        $"\"{CurrentExePath}\" {LaunchArgs}";

    /// <summary>是否已开启开机自启（键值存在且路径匹配当前 exe）</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key?.GetValue(AppName) is not string value) return false;
            // 仅比对 exe 路径部分（忽略参数差异），确保移动/重命名后状态正确
            var path = value.Trim().Trim('"').Split(' ')[0];
            return !string.IsNullOrEmpty(path) &&
                   path.Equals(CurrentExePath, System.StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开启开机自启：写入 Run 键值</summary>
    public static bool Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.SetValue(AppName, DesiredValue, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>关闭开机自启：删除 Run 键值</summary>
    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(AppName) != null)
                key.DeleteValue(AppName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
