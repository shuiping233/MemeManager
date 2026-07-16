using Microsoft.Win32;
using System;
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

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var raw = key?.GetValue(AppName);
            if (raw is not string value)
                return false;
            // 仅比对 exe 路径部分（路径可能含空格，不能按空格 Split），
            // 去首尾引号后截取到 ".exe" 为止，忽略后面的参数。
            var unquoted = value.Trim().Trim('"');
            int exeIdx = unquoted.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            var path = exeIdx >= 0 ? unquoted.Substring(0, exeIdx + 4) : unquoted;
            return !string.IsNullOrEmpty(path) &&
                   path.Equals(CurrentExePath, System.StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Startup] 检查注册表开机自启设置异常: {ex.Message}");
            return false;
        }
    }

    public static bool Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return false;
            key.SetValue(AppName, DesiredValue, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Startup] 写入注册表开机自启设置异常: {ex.Message}");
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return false;
            if (key.GetValue(AppName) != null)
                key.DeleteValue(AppName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Startup] Disable 异常: {ex.Message}");
            return false;
        }
    }
}
