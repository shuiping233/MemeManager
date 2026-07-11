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
            var raw = key?.GetValue(AppName);
            if (raw is not string value)
            {
                Logger.Log($"[Startup] 检查注册表开机自启设置: key={RunKey}\\{AppName} 未找到(string)值, raw={raw}, 当前exe={CurrentExePath} => false");
                return false;
            }
            // 仅比对 exe 路径部分（忽略参数差异），确保移动/重命名后状态正确。
            // 注意：值形如 "\"C:\x\MemeManager.exe\" --hidden"，需先去掉所有引号再取第一段。
            var path = value.Replace("\"", "").Split(' ')[0];
            bool matched = !string.IsNullOrEmpty(path) &&
                           path.Equals(CurrentExePath, System.StringComparison.OrdinalIgnoreCase);
            Logger.Log($"[Startup] 检查注册表开机自启设置: key={RunKey}\\{AppName}, 读取value=\"{value}\", 解析path=\"{path}\", 当前exe=\"{CurrentExePath}\", matched={matched} => {matched}");
            return matched;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Startup] 检查注册表开机自启设置异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>开启开机自启：写入 Run 键值</summary>
    public static bool Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null)
            {
                Logger.Log($"[Startup] 写入注册表开机自启设置失败: 无法打开可写注册表键 {RunKey}");
                return false;
            }
            key.SetValue(AppName, DesiredValue, RegistryValueKind.String);
            Logger.Log($"[Startup] 写入注册表开机自启设置: key={RunKey}\\{AppName}, value=\"{DesiredValue}\", 当前exe=\"{CurrentExePath}\"");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Startup] 写入注册表开机自启设置异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>关闭开机自启：删除 Run 键值</summary>
    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null)
            {
                Logger.Log($"[Startup] Disable 失败: 无法打开可写注册表键 {RunKey}");
                return false;
            }
            var existed = key.GetValue(AppName) != null;
            if (existed)
                key.DeleteValue(AppName, throwOnMissingValue: false);
            Logger.Log($"[Startup] Disable: key={RunKey}\\{AppName}, 删除前存在={existed}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Startup] Disable 异常: {ex.Message}");
            return false;
        }
    }
}
