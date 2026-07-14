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

    // 快捷键修饰键：MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8
    public uint HotKeyModifiers { get; set; } = 3; // 默认 Ctrl(2) + Alt(1)
    public ushort HotKeyVk { get; set; } = 0xBE;   // 默认 . (OEM_PERIOD)

    // 是否将日志写入数据目录下的 log/debug.log
    public bool SaveLogFile { get; set; } = false;

    // 窗口尺寸持久化（关闭/退出前记录，下次启动还原）
    public WindowSizePreset WindowSizePreset { get; set; } = WindowSizePreset.Default;

    public double WindowWidth { get; set; } = 950;
    public double WindowHeight { get; set; } = 750;
    public bool WindowMaximized { get; set; } = false;

    // 悬停预览图最大分辨率（超过则等比压缩）
    public double PreviewMaxWidth { get; set; } = 640;
    public double PreviewMaxHeight { get; set; } = 480;

    // 悬停预览触发延时（毫秒）
    public int PreviewDelayMs { get; set; } = 500;

    public bool AutoStart { get; set; } = false;

    // 是否启用“控件复用策略”：复用 VM 与列表容器（秒开、不抖动，但后台内存常驻较高）。
    // 关闭（默认）：每次刷新/切分类整体重建 VM，旧 Image 控件从树消失后框架自动释放
    // GPU 纹理，后台内存能显著回落；代价是每次重建需重新解码图片。
    public bool UseControlReuse { get; set; } = false;

    // 拖出图片时是否以图片格式输出（仅单张生效）：
    // 关闭（默认）：只写文件格式，老版本 QQ 会显示成文件。
    // 开启：单张拖出时额外写入图片格式，老版本 QQ 识别为图片；多张仍走文件格式。
    public bool DragOutputAsImage { get; set; } = false;
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

