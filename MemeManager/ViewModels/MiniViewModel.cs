using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemeManager.Infrastructure;
using MemeManager.Models;
using Microsoft.Extensions.DependencyInjection;

namespace MemeManager.ViewModels;

// Mini 模式的 ViewModel：仅承载"明确用户意图"的命令，纯 UI 状态（分类下拉/Picker/导入提示）
// 与窗口模式切换仍留在 MiniPage code-behind（见 Phase 2.10 方案 A 范围）。
public partial class MiniViewModel(MemeDataEngine engine) : ObservableObject
{

    // 切回完整模式：VM 只发请求，实际的 Window 模式切换由 Page 接管后调用 App.MainWindow.SwitchMode。
    // 用委托属性（非 event）：单 Page 对接场景下 '=' 赋值天然不累积，避免单例 VM 事件订阅泄漏。
    public Action? ExpandToFullRequested { get; set; }

    [RelayCommand]
    private void Expand()
        => ExpandToFullRequested?.Invoke();

    // 从 Picker 点选表情发送到外部窗口：VM 只发请求（需经 Page 解析外部窗口句柄），不引用 Window。
    public Action<MemeViewModel>? SendToExternalRequested { get; set; }

    [RelayCommand]
    private void SendMeme(MemeViewModel vm)
        => SendToExternalRequested?.Invoke(vm);
}
