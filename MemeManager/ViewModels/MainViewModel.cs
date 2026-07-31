using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemeManager.Infrastructure;
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

    // 切换到 Mini 模式请求：MainPage 订阅并调用 MainWindow.SwitchMode（UI 行为留 Page 层）
    public event Action? MiniModeRequested;

    [RelayCommand]
    private void SwitchToMiniMode()
    {
        MiniModeRequested?.Invoke();
    }

    // 切换编辑（多选）模式请求：MainPage 订阅并执行 ToggleEditMode（UI 行为留 Page 层）
    public event Action? EditModeRequested;

    [RelayCommand]
    private void ToggleEditMode()
    {
        EditModeRequested?.Invoke();
    }

    // 全选/取消全选请求：MainPage 订阅并执行 ToggleSelectAll（UI 行为留 Page 层）
    public event Action? SelectAllRequested;

    [RelayCommand]
    private void SelectAll()
    {
        SelectAllRequested?.Invoke();
    }

    // 新建分类请求：MainPage 订阅并执行 ShowAddCategoryDialog（UI 行为留 Page 层）。
    // 与 2.7 分类右键的 CategoryNew_Click 共用此入口。
    public event Action? NewCategoryRequested;

    [RelayCommand]
    private void NewCategory()
    {
        NewCategoryRequested?.Invoke();
    }

    // 分类页业务（2.7）：直接注入 MemeDataEngine 单例（过渡方案，后续统一改构造器注入）。
    // 仅做业务判断 + 调 DataEngine + 维护 CategoryList 集合；弹窗类 UI 行为通过下方事件请求 Page 层。
    private readonly MemeDataEngine _engine = App.GetService<MemeDataEngine>();

    // 分类数据变更后通知 Page 刷新表情列表（触发 RefreshMemes）。
    public event Action? CategoriesChangedRequested;

    // 删除分类确认弹窗请求：Page 订阅，用 DialogHelper 弹确认框，返回用户是否确认。
    public event Func<CategoryViewModel, Task<bool>>? ConfirmDeleteCategoryRequested;

    // 重命名分类输入弹窗请求：Page 订阅，用 DialogHelper 弹输入框，返回用户输入（取消/空白返回 null）。
    public event Func<CategoryViewModel, Task<string?>>? PromptRenameCategoryRequested;

    // 重命名分类失败提示请求：Page 订阅，用 DialogHelper 弹失败提示。
    public event Action<CategoryViewModel>? RenameCategoryFailedRequested;

    // 在文件资源管理器中打开分类对应文件夹（纯逻辑，无需弹窗）。
    [RelayCommand]
    private void OpenCategoryFolder(CategoryViewModel cat)
    {
        var dir = System.IO.Path.Combine(_engine.BaseDir, cat.Name);
        Utils.OpenInExplorer(dir, select: false, logTag: "打开分类文件夹");
    }

    // 删除分类：确认弹窗 → 调 DataEngine 删除 → 维护 CategoryList 集合 → 切换当前分类 → 通知刷新。
    [RelayCommand]
    private async Task DeleteCategoryAsync(CategoryViewModel cat)
    {
        if (ConfirmDeleteCategoryRequested == null || !await ConfirmDeleteCategoryRequested(cat))
            return;

        bool ok = await _engine.DeleteCategoryAsync(cat.Name);
        if (!ok) return;

        for (int i = CategoryList.Count - 1; i >= 0; i--)
            if (CategoryList[i].Name.Equals(cat.Name, StringComparison.OrdinalIgnoreCase))
                CategoryList.RemoveAt(i);

        if (CurrentCategory.Equals(cat.Name, StringComparison.OrdinalIgnoreCase))
        {
            CurrentCategory = CategoryList.FirstOrDefault()?.Name ?? string.Empty;
        }

        CategoriesChangedRequested?.Invoke();
    }

    // 重命名分类：输入弹窗 → 同名校验 → 调 DataEngine 改名 → 同步 VM 属性与当前分类 → 通知刷新。
    [RelayCommand]
    private async Task RenameCategoryAsync(CategoryViewModel cat)
    {
        if (PromptRenameCategoryRequested == null) return;
        var newName = await PromptRenameCategoryRequested(cat);
        if (string.IsNullOrWhiteSpace(newName)) return;

        if (newName.Equals(cat.Name, StringComparison.OrdinalIgnoreCase)
            || CategoryList.Any(c => c.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
        {
            RenameCategoryFailedRequested?.Invoke(cat);
            return;
        }

        bool ok = await _engine.RenameCategoryAsync(cat.Name, newName);
        if (!ok)
        {
            RenameCategoryFailedRequested?.Invoke(cat);
            return;
        }

        cat.Name = newName;
        if (CurrentCategory.Equals(cat.Name, StringComparison.OrdinalIgnoreCase))
            CurrentCategory = newName;

        CategoriesChangedRequested?.Invoke();
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
