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

    // 是否对后台工作线程启用 EcoQoS（Windows 线程级效率模式），降低后台 CPU/能耗。
    // 仅作用于后台线程，不影响 UI 响应；老版本 Windows 上会被忽略。
    public bool EcoMode { get; set; } = true;

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

    // 多选操作模式：
    // false（默认）：资源管理器风格，使用 ListViewSelectionMode.Multiple（系统自带复选框），
    //                 隐藏我们自绘的右上角复选框。
    // true：使用 ListViewSelectionMode.Extended + 自绘右上角复选框，支持 shift 连续/反选。
    public bool ExplorerStyleMultiSelect { get; set; } = false;

    // StorageFile 拖拽支持（拖出为文件，恢复动态 GIF 等到 QQ 的能力）：
    // false（默认）：禁用。拖出仅用进程内 Bitmap 流（稳定，但 GIF 拖到 QQ 会变静态图）。
    // true：启用。拖出时写入 StorageFile，支持作为文件拖出（动态 GIF 正常），
    //       但快速连续拖拽可能触发跨公寓 COM 释放导致程序闪退。
    public bool StorageFileDrag { get; set; } = false;
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

