using MemeManager.ViewModels;

namespace MemeManager.Models;

/// <summary>
/// 复用策略：增量更新，复用已有 VM 与 ListView/GridView 容器。
/// - 刷新/切分类只在内容变化的项上换源，不整体销毁容器（保持滚动条位置、零抖动）。
/// - 拖拽重排用“锚点对齐”，把被拖组平移到鼠标落点处（而非 WinUI 默认的组尾对齐）。
/// 代价：VM 与其持有的 BitmapImage 长期被 _memeList 字段引用，后台内存常驻较高。
/// </summary>
public sealed class ReuseStrategy : IMemeListStrategy
{
    public void RefreshMemes(ICollection<MemeViewModel> list, IEnumerable<MemeModel> memes)
    {
        var memeArr = memes.ToList();
        int newCount = memeArr.Count;

        if (list is not IList<MemeViewModel> l)
        {
            list.Clear();
            foreach (var m in memeArr) list.Add(new MemeViewModel(m));
            return;
        }

        int oldCount = l.Count;
        if (oldCount == 0)
        {
            foreach (var m in memeArr) l.Add(new MemeViewModel(m));
            return;
        }

        int common = Math.Min(oldCount, newCount);
        for (int i = 0; i < common; i++)
        {
            if (!l[i].FileName.Equals(memeArr[i].FileName, StringComparison.OrdinalIgnoreCase))
                l[i].UpdateModel(memeArr[i]);
        }

        if (newCount > oldCount)
        {
            for (int i = oldCount; i < newCount; i++) l.Add(new MemeViewModel(memeArr[i]));
        }
        else if (newCount < oldCount)
        {
            for (int i = oldCount - 1; i >= newCount; i--) l.RemoveAt(i);
        }
    }

    public List<string>? ComputeDragOrder(IList<MemeViewModel> list, IEnumerable<MemeModel> draggingGroup, string? anchorFileName)
    {
        var draggedGroup = draggingGroup.ToList();
        if (anchorFileName == null || draggedGroup.Count == 0) return null;

        var groupNames = new HashSet<string>(draggedGroup.Select(m => m.FileName), StringComparer.OrdinalIgnoreCase);
        int anchorIdx = list
            .Select((m, i) => new { m, i })
            .FirstOrDefault(x => x.m.FileName.Equals(anchorFileName, StringComparison.OrdinalIgnoreCase))?.i ?? -1;
        if (anchorIdx < 0) return null;

        // 落点插入位置 = 锚点项之前、且不属于被拖组的元素个数
        int insertAt = 0;
        for (int i = 0; i < anchorIdx; i++)
            if (!groupNames.Contains(list[i].FileName)) insertAt++;

        var others = list.Where(m => !groupNames.Contains(m.FileName)).ToList();
        var groupVms = draggedGroup
            .Select(n => list.FirstOrDefault(m => m.FileName.Equals(n.FileName, StringComparison.OrdinalIgnoreCase)))
            .OfType<MemeViewModel>()
            .ToList();

        var newOrder = new List<MemeViewModel>();
        newOrder.AddRange(others.Take(insertAt));
        newOrder.AddRange(groupVms);
        newOrder.AddRange(others.Skip(insertAt));

        return newOrder.Select(m => m.FileName).ToList();
    }
}
