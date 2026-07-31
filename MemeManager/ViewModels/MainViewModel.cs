using CommunityToolkit.Mvvm.ComponentModel;
using MemeManager.Models;
using MemeManager.ViewModels;
using System.Collections.ObjectModel;

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

    // 当前分类下的表情列表（绑定到 GridView），ReadOnly 集合，仅内部增删改
    public ObservableCollection<MemeViewModel> MemeList { get; } = new();

    // 左侧分类列表（绑定到分类栏），ReadOnly 集合，仅内部增删改
    public ObservableCollection<CategoryViewModel> CategoryList { get; } = new();

    public MainViewModel()
    {
    }
}
