using System;
using System.Collections.Generic;
using System.Linq;
using MemeManager.Models;
using MemeManager.ViewModels;

namespace MemeManager;

/// <summary>
/// 重建策略：每次刷新/切分类都整体 Clear+重建 VM（旧 Image 控件随旧容器从可视化树消失，
/// WinUI 框架会在下一帧自动释放其 GPU 纹理）。隐藏后台内存能显著回落，
/// 代价是每次重建会重新解码图片（秒开感略弱，但体感通常可接受）。
/// 拖拽重排使用 WinUI 内置的默认对齐（组尾），不做锚点对齐。
/// </summary>
public sealed class RebuildStrategy : IMemeListStrategy
{
    public void SyncCategories(ICollection<CategoryViewModel> list, IEnumerable<string> categories, Func<string, int> getCount)
    {
        list.Clear();
        foreach (var cat in categories)
            list.Add(new CategoryViewModel(cat, getCount(cat)));
    }

    public void RefreshMemes(ICollection<MemeViewModel> list, IEnumerable<MemeModel> memes)
    {
        list.Clear();
        foreach (var m in memes)
            list.Add(new MemeViewModel(m));
    }

    public List<string>? ComputeDragOrder(IList<MemeViewModel> list, IEnumerable<MemeModel> draggingGroup, string? anchorFileName)
    {
        // 重建模式不做锚点对齐：直接沿用 WinUI 重排后的列表当前顺序。
        return null;
    }
}
