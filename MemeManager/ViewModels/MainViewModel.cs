using CommunityToolkit.Mvvm.ComponentModel;
using MemeManager.Models;

namespace MemeManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // 当前视图所属的分类类型（全部表情 / 普通分类），纯 UI 视图状态
    [ObservableProperty]
    public partial CategoryKind CurrentCategoryKind { get; set; } = CategoryKind.Normal;

    public MainViewModel()
    {
    }
}
