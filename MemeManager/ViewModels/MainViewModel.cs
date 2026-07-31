using CommunityToolkit.Mvvm.ComponentModel;
using MemeManager.Models;

namespace MemeManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // 当前视图所属的分类类型（全部表情 / 普通分类），纯 UI 视图状态
    [ObservableProperty]
    public partial CategoryKind CurrentCategoryKind { get; set; } = CategoryKind.Normal;

    // 当前选中的分类名（空串 = 全部表情视图），纯 UI 视图状态
    [ObservableProperty]
    public partial string CurrentCategory { get; set; } = string.Empty;

    // 多选（批量操作）模式开关，纯 UI 视图状态
    [ObservableProperty]
    public partial bool EditMode { get; set; }

    public MainViewModel()
    {
    }
}
