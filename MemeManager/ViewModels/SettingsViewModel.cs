using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemeManager.Infrastructure;
using MemeManager.Services;
using Microsoft.UI.Dispatching;

namespace MemeManager.ViewModels;

// 设置页 ViewModel：仅承载"明确用户意图"的命令；配置双向绑定/UI 状态（Toggle/文本框/热键录制）
// 仍留 SettingsPage code-behind（见 Phase 2.11 方案 A 范围）。涉及 Window/文件选择器/XamlRoot 的
// 副作用经事件回 Page 执行。
public partial class SettingsViewModel : ObservableObject
{
    private readonly UpdateService _updateService;

    // UpdateService 的后台任务在线程池线程上完成并触发 INPC（其内部全部 ConfigureAwait(false)），
    // 而 x:Bind 的 OneWay 绑定收到 PropertyChanged 后会直接更新 UI 控件——UI 控件只能在 UI 线程
    // 访问，否则抛 COMException(0x8001010E)。因此转发必须封送到 UI 线程再触发。
    // VM 单例在 SettingsPage 构造（UI 线程）时首次创建，此处捕获的即 UI 线程 DispatcherQueue。
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    public SettingsViewModel(UpdateService updateService)
    {
        _updateService = updateService;
        // 单例 VM 订阅单例服务：全程只订阅一次，无累积问题，无需反订阅。
        // 启动时后台检查 / 手动刷新都会驱动 UpdateService.PropertyChanged，此处统一转发给 UI。
        _updateService.PropertyChanged += OnUpdateServicePropertyChanged;
    }

    private void OnUpdateServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 后台线程触发 → 封送到 UI 线程再转发；已在 UI 线程则直接转发。
        if (_dispatcher.HasThreadAccess)
            ForwardUpdateServiceChange(e);
        else
            _dispatcher.TryEnqueue(() => ForwardUpdateServiceChange(e));
    }

    private void ForwardUpdateServiceChange(PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UpdateService.LatestVersion):
                OnPropertyChanged(nameof(LatestVersion));
                OnPropertyChanged(nameof(CheckStateText));
                break;
            case nameof(UpdateService.HasNewVersion):
                OnPropertyChanged(nameof(HasNewVersion));
                break;
            case nameof(UpdateService.CheckState):
                OnPropertyChanged(nameof(CheckState));
                OnPropertyChanged(nameof(CheckStateText));
                break;
            case nameof(UpdateService.IsChecking):
                OnPropertyChanged(nameof(IsChecking));
                break;
        }
    }

    public string LatestVersion => _updateService.LatestVersion ?? "-";

    public string CurrentVersion => Utils.GetInformationalVersion();

    public bool HasNewVersion => _updateService.HasNewVersion;

    public UpdateCheckState CheckState => _updateService.CheckState;

    public bool IsChecking => _updateService.IsChecking;

    // 检查状态文案（随状态/语言变化，UI 绑定）
    public string CheckStateText => BuildCheckStateText();

    private string BuildCheckStateText()
    {
        // 请求失败优先提示失败（与当前版本是否 dev 无关）
        if (CheckState == UpdateCheckState.Failed)
            return Localization.Get("Settings_UpdateCheck_State_Failed");

        // 当前版本是开发版（无语义版本号，如本地 "… dev build"）：无法判断更新状态，
        // 显示专属文案；最新版本号等其余信息照常展示。
        if (VersionString.IsDevBuild(CurrentVersion))
            return Localization.Get("Settings_UpdateCheck_State_DevBuild");

        return CheckState switch
        {
            UpdateCheckState.Checking => Localization.Get("Settings_UpdateCheck_State_Checking"),
            UpdateCheckState.UpToDate => Localization.Get("Settings_UpdateCheck_State_UpToDate"),
            UpdateCheckState.HasUpdate => string.Format(
                Localization.Get("Settings_UpdateCheck_State_HasUpdate"), LatestVersion),
            _ => Localization.Get("Settings_UpdateCheck_State_Idle"),
        };
    }

    // 语言切换后重算动态文案（供设置页 LanguageComboBox 切换后调用）
    public void RefreshLocalizedTexts() => OnPropertyChanged(nameof(CheckStateText));

    // 手动检查更新：await 编排完成后状态经 PropertyChanged 自动刷新 UI
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            await _updateService.CheckAsync();
        }
        catch (Exception ex)
        {
            // 防御：AsyncRelayCommand 默认会把异常 rethrow 到 UI 线程，
            // 触发全局 UnhandledException 崩溃处理（弹窗+退出），必须兜底只记日志。
            Logger.Log($"[Settings] 检查更新异常: {ex}");
        }
    }

    // 打开EXE所在文件夹
    [RelayCommand]
    private async Task OpenExeFolderAsync()
    {
        var path = Utils.GetExeDirectory();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(path);
            await Windows.System.Launcher.LaunchFolderPathAsync(path);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] 打开EXE文件夹错误: {ex.Message}");
        }
    }

    // 打开配置文件夹：
    [RelayCommand]
    private async Task OpenConfigFolderAsync()
    {
        var path = AppConstants.AppDataDirPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(path);
            await Windows.System.Launcher.LaunchFolderPathAsync(path);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] 打开配置文件夹错误: {ex.Message}");
        }
    }

    // 打开日志文件夹：纯 Launcher 调用，可直接进 VM。日志位于数据目录下的 log/。
    [RelayCommand]
    private async Task OpenLogFolderAsync()
    {
        var baseDir = App.DataEngine?.BaseDir;
        if (string.IsNullOrWhiteSpace(baseDir)) return;
        var logDir = Path.Combine(baseDir, "log");
        try
        {
            Directory.CreateDirectory(logDir);
            await Windows.System.Launcher.LaunchFolderPathAsync(logDir);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] 打开日志文件夹错误: {ex.Message}");
        }
    }

    // 反馈建议：直接用系统默认浏览器打开 GitHub Issues 页（不内嵌 WebView）。
    [RelayCommand]
    private async Task FeedbackAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/shuiping233/MemeManager/issues"));
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] 打开反馈页错误: {ex.Message}");
        }
    }

    // 前往下载新版本：GitHub Releases 页（纯 Launcher 调用，可直接进 VM）。
    [RelayCommand]
    private async Task OpenGithubReleaseAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/shuiping233/MemeManager/releases"));
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] 打开 GitHub Releases 页错误: {ex.Message}");
        }
    }

    // 前往下载新版本：CNB Releases 页（纯 Launcher 调用，可直接进 VM）。
    [RelayCommand]
    private async Task OpenCnbReleaseAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://cnb.cool/shuiping233/MemeManager/-/releases"));
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] 打开 CNB Releases 页错误: {ex.Message}");
        }
    }

    // 关于：需弹窗（依赖 XamlRoot），经回调回 Page 执行。
    // 用委托属性而非 event：单例 VM 每次打开设置页由 Page 用 '=' 覆盖，不累积、无需反订阅。
    public Action? AboutRequested { get; set; }

    [RelayCommand]
    private void About()
        => AboutRequested?.Invoke();

    // 退出程序：需弹窗（依赖 XamlRoot），经回调回 MainWindow 执行。
    public Action? ProgramExitRequested { get; set; }

    [RelayCommand]
    private void ProgramExit()
    {
        ProgramExitRequested?.Invoke();
    }

    // 浏览选目录并立即保存：依赖文件选择器 + MainWindow 状态，经回调回 Page。
    public Action? BrowseFolderRequested { get; set; }

    [RelayCommand]
    private void BrowseFolder()
        => BrowseFolderRequested?.Invoke();

    // 打开数据文件夹：路径来自 UI 文本框（UI 状态），经回调把路径回 Page 打开。
    public Action<string>? OpenFolderRequested { get; set; }

    [RelayCommand]
    private void OpenMemeDataFolder(string path)
        => OpenFolderRequested?.Invoke(path);

    // 关闭设置浮窗：UI 行为，经回调回 Page。
    public Action? CloseRequested { get; set; }

    [RelayCommand]
    private void Close()
        => CloseRequested?.Invoke();
}
