using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemeManager.Infrastructure;
using MemeManager.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MemeManager.ViewModels;

// 设置页 ViewModel：仅承载"明确用户意图"的命令；配置双向绑定/UI 状态（Toggle/文本框/热键录制）
// 仍留 SettingsPage code-behind（见 Phase 2.11 方案 A 范围）。涉及 Window/文件选择器/XamlRoot 的
// 副作用经事件回 Page 执行。
public partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(MemeDataEngine engine)
    {
    }

    // 打开配置文件夹：纯 Launcher 调用，可直接进 VM（依赖 MainWindow.AppDataDir 静态访问）。
    [RelayCommand]
    private async Task OpenConfigFolderAsync()
    {
        var path = MainWindow.AppDataDir;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            System.IO.Directory.CreateDirectory(path);
            await Windows.System.Launcher.LaunchFolderPathAsync(path);
        }
        catch (System.Exception ex)
        {
            Logger.Log($"[Settings] 打开配置文件夹错误: {ex.Message}");
        }
    }

    // 浏览选目录并立即保存：依赖文件选择器 + MainWindow 状态，经事件回 Page。
    public event Action? BrowseFolderRequested;

    [RelayCommand]
    private void BrowseFolder()
        => BrowseFolderRequested?.Invoke();

    // 打开数据文件夹：路径来自 UI 文本框（UI 状态），经事件把路径回 Page 打开。
    public event Action<string>? OpenFolderRequested;

    [RelayCommand]
    private void OpenMemeDataFolder(string path)
        => OpenFolderRequested?.Invoke(path);

    // 关闭设置浮窗：UI 行为，经事件回 Page。
    public event Action? CloseRequested;

    [RelayCommand]
    private void Close()
        => CloseRequested?.Invoke();
}
