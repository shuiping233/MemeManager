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
}
