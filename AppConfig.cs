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
    public uint HotKeyModifiers { get; set; } = 1; // 默认 Alt
    public ushort HotKeyVk { get; set; } = 0x45;   // 默认 E
}
