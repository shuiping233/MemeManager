using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemeManager.Models;
using MemeManager.ViewModels;
using System.Collections.ObjectModel;

namespace MemeManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // 刷新请求：MainPage 订阅并执行业务逻辑（RefreshDataAsync），VM 只负责暴露 Command 入口。
    // 这样 Phase 2 只迁入口、不搬业务（业务拆 Service 留到 Phase 3）。
    public event Func<Task>? RefreshRequested;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (RefreshRequested != null)
            await RefreshRequested.Invoke();
    }

    // 设置浮窗请求：MainPage 订阅并弹出 SettingsFlyout（UI 行为留 Page 层）
    public event Action? SettingsRequested;

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsRequested?.Invoke();
    }

    // 当前视图所属的分类类型（全部表情 / 普通分类），纯 UI 视图状态
    [ObservableProperty]
    public partial CategoryKind CurrentCategoryKind { get; set; } = CategoryKind.Normal;

    // 当前选中的分类名（空串 = 全部表情视图），纯 UI 视图状态
    [ObservableProperty]
    public partial string CurrentCategory { get; set; } = string.Empty;

    // 多选（批量操作）模式开关，纯 UI 视图状态
    [ObservableProperty]
    public partial bool EditMode { get; set; }

    // 当前分类下的表情列表（绑定到 GridView），ReadOnly 集合，仅内部增删改
    public ObservableCollection<MemeViewModel> MemeList { get; } = new();

    // 左侧分类列表（绑定到分类栏），ReadOnly 集合，仅内部增删改
    public ObservableCollection<CategoryViewModel> CategoryList { get; } = new();

    // “全部表情”虚拟项（左侧栏固定头项，Name 空串代表全部表情）
    public CategoryViewModel AllMemesVm { get; } = new("", 0);

    // 内部拖拽移动时暂存被拖的 meme 模型列表（非空即表示内部拖拽，区别于外部导入）
    public List<MemeModel>? DraggingMemes { get; set; }

    // 右键菜单上下文：当前右键选中的表情
    public MemeViewModel? ContextMeme { get; set; }

    // 右键菜单上下文：当前右键选中的分类
    public CategoryViewModel? ContextCategory { get; set; }

    // 全量重载（F5）进行中标记：防止重载与自身/后台写任务并发重建缓存导致崩溃
    public bool Reloading { get; set; }

    // 搜索框防抖定时器（输入停止 150ms 后触发刷新）
    public Microsoft.UI.Xaml.DispatcherTimer? SearchDebounceTimer { get; set; }

    // 悬停放大预览：当前待显示（已延迟、尚未弹出）的表情项
    public MemeViewModel? PendingPreviewVm { get; set; }

    // 悬停放大预览：预览浮窗锚定的 UI 元素（鼠标所在项）
    public Microsoft.UI.Xaml.FrameworkElement? PendingPreviewAnchor { get; set; }

    // 拖拽重排锚点：本次拖起的那一张（e.Items[0]）的文件名
    public string? DragAnchorFileName { get; set; }

    // 防止粘贴导入的分类对话框重入
    public bool PasteDialogOpen { get; set; }

    // 多选模式：Shift 连续选择的锚点（在 _memeList 中的索引）
    public int LastShiftAnchor { get; set; } = -1;

    public MainViewModel()
    {
    }
}
