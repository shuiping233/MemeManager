using MemeManager.Infrastructure;

namespace MemeManager.Models;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

// UI 模式：Full=完整管理模式，Mini=紧凑快速调用模式
public enum AppMode
{
    Full = 0,
    Mini = 1
}

// Model 层不引用 WinUI，映射在 UI 层完成。
public enum ImageStretchMode
{
    None,
    Fill,
    Uniform,
    UniformToFill
}

public static class ImageStretchModeHelper
{
    public const string Default = nameof(ImageStretchMode.UniformToFill);

    public static ImageStretchMode Parse(string? s) => s switch
    {
        nameof(ImageStretchMode.None) => ImageStretchMode.None,
        nameof(ImageStretchMode.Fill) => ImageStretchMode.Fill,
        nameof(ImageStretchMode.Uniform) => ImageStretchMode.Uniform,
        nameof(ImageStretchMode.UniformToFill) => ImageStretchMode.UniformToFill,
        _ => ImageStretchMode.UniformToFill,
    };
}

// 注意：本类型用 record 而非 class，依赖其天然的基于所有字段的值相等（==/Equals），
// 供 ConfigService 判断“配置是否真的变化”以跳过无谓写盘。
// 若以后需要新增 List<T>/Dictionary 等集合字段，record 默认的相等仅比较引用、不会比较内容，
// 会导致内容变化但引用未变时被误判为未变化而不写盘；届时需将集合改为不可变类型或自定义 Equals。
public record AppConfig
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    // 界面语言："system"=跟随系统(默认)，"zh-CN"，"en-US" 等。运行时由 WinUI3Localizer 切换。
    public string? Language { get; set; } = "system";

    public string StoragePath { get; set; } = string.Empty;

    public string LastCategory { get; set; } = string.Empty;

    // 是否启用全局呼出快捷键（默认开）。关闭后设置页禁用快捷键录入控件，
    // 启动与保存配置时都会按此值注册/注销全局热键。
    public bool EnableHotKey { get; set; } = true;

    // 快捷键修饰键：MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8
    public uint HotKeyModifiers { get; set; } = 3; // 默认 Ctrl(2) + Alt(1)
    public ushort HotKeyVk { get; set; } = 0xBE;   // 默认 . (OEM_PERIOD)

    // 是否将日志写入数据目录下的 log/debug.log
    public bool SaveLogFile { get; set; } = false;

    // 是否对后台工作线程启用 EcoQoS（Windows 线程级效率模式），降低后台 CPU/能耗。
    // 仅作用于后台线程，不影响 UI 响应；老版本 Windows 上会被忽略。
    public bool EcoMode { get; set; } = true;

    public double WindowWidth { get; set; } = 950;
    public double WindowHeight { get; set; } = 750;
    public bool WindowMaximized { get; set; } = false;

    // 上次退出时的 UI 模式（Full/Mini），启动时恢复
    public AppMode LastAppMode { get; set; } = AppMode.Full;

    // 是否允许进入 Mini（紧凑）模式。关闭后：设置页不再提供开关、主窗口 Mini 按钮移除、
    // 托盘“切换窗口模式”菜单项禁用，启动时若 config 记录为 Mini 也会强制回退到 Full。
    public bool AllowMiniMode { get; set; } = true;

    // 悬停预览图最大分辨率（超过则等比压缩）
    public double PreviewMaxWidth { get; set; } = AppConstants.PreviewMaxWidth;
    public double PreviewMaxHeight { get; set; } = AppConstants.PreviewMaxHeight;

    // 悬停预览触发延时（毫秒）
    public int PreviewDelayMs { get; set; } = 500;

    // 表情列表/缩略图 Image 的拉伸方式（config.json 存字符串，默认 UniformToFill，
    // 与旧硬编码一致；用户手改非法值由 ImageStretchModeHelper.Parse 回退默认）。
    public string ImageStretch { get; set; } = ImageStretchModeHelper.Default;

    // 左侧分类栏宽度（可在主界面拖动调整）。
    public double CategoryWidth { get; set; } = AppConstants.DefaultCategoryWidth;

    // 是否允许显示分类栏宽度分隔条（可在主界面拖动调整）
    public bool CategorySplitterEnabled { get; set; } = true;

    public bool AutoStart { get; set; } = false;

    // 启动时后台检查是否有新版本（默认关闭，避免打扰用户）：
    // 开启后启动时后台请求一次更新检查，结果仅在设置页"前往下载新版本"处展示，
    // 不弹任何提示；关闭时只有用户点开该按钮并手动"检查更新"才会发起请求。
    public bool AutoCheckForUpdates { get; set; } = false;

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
    //       使用异步 SetProvider 方案后, 未见用户反馈拖拽时会闪退崩溃了, 估保持默认开启
    public bool StorageFileDrag { get; set; } = true;
}
