namespace MemeManager.Infrastructure;

public static class AppConstants
{
    // 应用显示名（标题条、对话框等共用）。
    public const string AppName = "MemeManager";

    // 配置/锁文件所在目录与文件名（%LOCALAPPDATA%\AppName 下），供各模块复用，避免散落字面量。
    public const string ConfigFileName = "config.json";
    public const string InstanceLockFileName = "instance.lock";
    public static string DefaultAppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    public static string ConfigPath => Path.Combine(DefaultAppDataDir, ConfigFileName);
    public static string InstanceLockPath => Path.Combine(DefaultAppDataDir, InstanceLockFileName);

    // AppIcon.ico 路径（发布/调试均会拷贝到 Assets 下），托盘图标与标题条 Logo 共用。
    public static string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
    public static string WindowTitle => $"{AppName} {Utils.GetInformationalVersion()}";

    public const string AllMemesCategory = "AllMemes";
}

