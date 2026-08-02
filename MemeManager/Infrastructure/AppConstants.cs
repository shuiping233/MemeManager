using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemeManager.Infrastructure;

public static class AppConstants
{
    // 应用显示名（标题条、对话框等共用）。
    public const string AppName = "MemeManager";

    // 配置/锁文件所在目录与文件名（%LOCALAPPDATA%\AppName 下），供各模块复用，避免散落字面量。
    public const string ConfigFileName = "config.json";
    public const string InstanceLockFileName = "instance.lock";

    // 注意, 这个是appdata目录, 默认表情包数据目录请用 DefaultMemeDataStoragePath
    public static string AppDataDirPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    public static string ConfigPath => Path.Combine(AppDataDirPath, ConfigFileName);
    public static string InstanceLockPath => Path.Combine(AppDataDirPath, InstanceLockFileName);

    // AppIcon.ico 路径（发布/调试均会拷贝到 Assets 下），托盘图标与标题条 Logo 共用。
    public static string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
    public static string WindowTitle => $"{AppName} {Utils.GetInformationalVersion()}";

    // 默认分类名（UI 初次启动、无任何分类时创建）。统一在此定义，避免 "Default" 字面量散落。
    public const string DefaultCategory = "Default";

    // 默认数据目录名（位于“图片”库或 LocalApplicationData 下）。统一在此定义，避免 "MeMeManagerData" 字面量散落。
    public const string DefaultMemeDataFolderName = "MeMeManagerData";

    public static string DefaultMemeDataStoragePath()
    {
        // 优先用“图片”库；若其为空/未配置（某些精简系统或域环境会返回空串），
        // 回退到 LocalApplicationData，避免拼接出相对路径或应用自身目录。
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures) || !Path.IsPathRooted(pictures))
            pictures = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(pictures, AppConstants.DefaultMemeDataFolderName);
    }

    // 分类元数据文件名（每个分类目录下的 .metadata.json）
    public const string MetadataFileName = ".metadata.json";

    // 分类名为空/非法时的兜底分类名（走 i18n），公开供 UI 层在“全部表情”视图下
    // 将外部拖入的图片归入此分类（而非误用视图标记值）。
    public static string UncategorizedCategory => Localization.Get("Category_Uncategorized");

    // 写盘 JSON：缩进可读 + 中文不转义（与引擎原配置序列化选项一致）
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 4,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    // 切分类写盘超级防抖时长：短时间内连续切换分类只落最后一次，避免每次切换都写 config。
    // 程序退出时由 MainWindow 调用 FlushLastCategory 立即落盘，避免丢失最后的选择。
    public static TimeSpan LastCategorySaveDebounce = TimeSpan.FromSeconds(3);
}

