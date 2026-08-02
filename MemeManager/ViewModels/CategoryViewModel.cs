using CommunityToolkit.Mvvm.ComponentModel;
using MemeManager.Infrastructure;

namespace MemeManager.ViewModels;

public partial class CategoryViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    // 下拉/列表显示名：空名(Name=="")代表"全部表情"虚拟项，统一显示 Category_AllMemes；
    // 否则显示真实分类名。供 Mini 的 ComboBox 等直接绑定。
    public string DisplayText =>
        string.IsNullOrEmpty(Name) ? Localization.Get("Category_AllMemes") : Name;

    public CategoryViewModel(string name, int count)
    {
        Name = name;
        Count = count;
    }
}
