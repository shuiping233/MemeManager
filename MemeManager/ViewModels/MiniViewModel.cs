using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemeManager.Infrastructure;
using MemeManager.Models;
using Microsoft.Extensions.DependencyInjection;

namespace MemeManager.ViewModels;

// Mini 模式的 ViewModel：仅承载"明确用户意图"的命令，纯 UI 状态（分类下拉/Picker/导入提示）
// 与窗口模式切换仍留在 MiniPage code-behind（见 Phase 2.10 方案 A 范围）。
public partial class MiniViewModel : ObservableObject
{
    private readonly MemeDataEngine _engine;

    public MiniViewModel(MemeDataEngine engine)
    {
        _engine = engine;
    }

    // 切回完整模式：VM 只发请求，实际的 Window 模式切换由 Page 订阅后调用 App.MainWindow.SwitchMode。
    public event Action? ExpandToFullRequested;

    [RelayCommand]
    private void Expand()
        => ExpandToFullRequested?.Invoke();

    // 从 Picker 点选表情发送到外部窗口：VM 只发请求（需经 Page 解析外部窗口句柄），不引用 Window。
    public event Action<MemeViewModel>? SendToExternalRequested;

    [RelayCommand]
    private void SendMeme(MemeViewModel vm)
        => SendToExternalRequested?.Invoke(vm);
}
