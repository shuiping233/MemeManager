using System.Text.Json.Serialization;

namespace MemeManager.Models;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

public class AppConfig
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public string StoragePath { get; set; } = string.Empty;

    public string LastCategory { get; set; } = string.Empty;

    // 全局呼出快捷键：修饰键（MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8）与虚拟键码
    public uint HotKeyModifiers { get; set; } = 3; // 默认 Ctrl(2) + Alt(1)
    public ushort HotKeyVk { get; set; } = 0xBE;   // 默认 . (OEM_PERIOD)

    // 是否将日志写入数据目录下的 log/debug.log（便于排查问题，注意日志文件大小）
    public bool SaveLogFile { get; set; } = false;

    // 窗口尺寸持久化：关闭/退出前记录，下次启动还原。
    // 尺寸预设枚举仅用于日志/调试展示，实际还原用具体宽高数值。
    public WindowSizePreset WindowSizePreset { get; set; } = WindowSizePreset.Default;

    public double WindowWidth { get; set; } = 950;
    public double WindowHeight { get; set; } = 750;
    public bool WindowMaximized { get; set; } = false;

    // 悬停预览图最大分辨率（超过则等比压缩）。默认 800x600。
    public double PreviewMaxWidth { get; set; } = 800;
    public double PreviewMaxHeight { get; set; } = 600;

    // 悬停预览触发延时（毫秒）。默认 400。
    public int PreviewDelayMs { get; set; } = 400;
}

// 窗口尺寸预设档位（仅作日志/调试展示，不限制实际可存分辨率）
public enum WindowSizePreset
{
    Default = 0,  // 950x750
    Small = 1,    // 720x560
    Medium = 2,   // 950x750
    Large = 3,    // 1200x900
    Maximized = 4
}

