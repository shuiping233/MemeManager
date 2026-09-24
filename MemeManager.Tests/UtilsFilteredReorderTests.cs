using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MemeManager.Infrastructure;
using Xunit;

namespace MemeManager.Tests;

// 过滤视图拖拽重排的"多集交换"工具测试。
// 核心语义：用户只能改变**可见项**之间的相对顺序，未显示的项必须保持原位。
public class UtilsFilteredReorderTests
{
    private static List<string> L(params string[] items) => items.ToList();

    // ---------- MergeSubsetOrder ----------

    [Fact]
    public void MergeSubsetOrder_DocumentedExample()
    {
        // full=[A,B,C,D,E,F]，过滤出 [B,D,E]（占位 1,3,4），用户拖成 [E,B,D]
        // → E 占位 1、B 占位 3、D 占位 4；未显示的 C 留在原位
        var result = Utils.MergeSubsetOrder(
            L("A", "B", "C", "D", "E", "F"),
            L("B", "D", "E"),
            L("E", "B", "D"));

        Assert.Equal(L("A", "E", "C", "B", "D", "F"), result);
    }

    [Fact]
    public void MergeSubsetOrder_NoFilter_EqualsReordered()
    {
        // 非搜索态（过滤视图 == 完整列表）：结果是拖拽后的顺序本身（与改动前行为等价）
        var full = L("A", "B", "C", "D");
        var reordered = L("D", "A", "C", "B");

        var result = Utils.MergeSubsetOrder(full, full, reordered);

        Assert.Equal(reordered, result);
    }

    [Fact]
    public void MergeSubsetOrder_HiddenItemsKeepTheirSlots()
    {
        // 过滤出首尾两项并拖成逆序：中间 B/C/D 不动
        var result = Utils.MergeSubsetOrder(
            L("A", "B", "C", "D", "E"),
            L("A", "E"),
            L("E", "A"));

        Assert.Equal(L("E", "B", "C", "D", "A"), result);
    }

    [Fact]
    public void MergeSubsetOrder_ContiguousSubset_SwapsWithinBlock()
    {
        var result = Utils.MergeSubsetOrder(
            L("A", "B", "C", "D", "E", "F"),
            L("C", "D", "E"),
            L("E", "C", "D"));

        Assert.Equal(L("A", "B", "E", "C", "D", "F"), result);
    }

    [Fact]
    public void MergeSubsetOrder_SingleItem_IsNoOp()
    {
        var full = L("A", "B", "C");
        Assert.Equal(full, Utils.MergeSubsetOrder(full, L("B"), L("B")));
    }

    [Fact]
    public void MergeSubsetOrder_UnchangedOrder_IsNoOp()
    {
        var full = L("A", "B", "C", "D");
        Assert.Equal(full, Utils.MergeSubsetOrder(full, L("B", "D"), L("B", "D")));
    }

    [Fact]
    public void MergeSubsetOrder_AllEmpty_ReturnsEmpty()
    {
        Assert.Empty(Utils.MergeSubsetOrder(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void MergeSubsetOrder_CustomComparer_IsUsed()
    {
        // 大小写不敏感比较：过滤项 "B"/"A" 命中 full 里的 "b"/"a"
        var result = Utils.MergeSubsetOrder(
            new List<string> { "a", "b", "c" },
            new List<string> { "B", "A" },
            new List<string> { "A", "B" },
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(new List<string> { "A", "B", "c" }, result);
    }

    [Fact]
    public void MergeSubsetOrder_DoesNotMutateInputs()
    {
        var full = L("A", "B", "C");
        var filtered = L("A", "C");
        var reordered = L("C", "A");

        Utils.MergeSubsetOrder(full, filtered, reordered);

        Assert.Equal(L("A", "B", "C"), full);
        Assert.Equal(L("A", "C"), filtered);
        Assert.Equal(L("C", "A"), reordered);
    }

    [Fact]
    public void MergeSubsetOrder_CountMismatch_Throws()
    {
        var ex = Assert.Throws<Utils.SubsetMismatchException>(
            () => Utils.MergeSubsetOrder(L("A", "B", "C"), L("A", "B"), L("A")));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void MergeSubsetOrder_ReorderedContainsUnknownItem_Throws()
    {
        Assert.Throws<Utils.SubsetMismatchException>(
            () => Utils.MergeSubsetOrder(L("A", "B", "C"), L("A", "B"), L("A", "Z")));
    }

    [Fact]
    public void MergeSubsetOrder_ReorderedMissesItem_Throws()
    {
        // 项数相同但不是同一批元素（[A,B] 与 [A,A]）
        Assert.Throws<Utils.SubsetMismatchException>(
            () => Utils.MergeSubsetOrder(L("A", "B", "C"), L("A", "B"), L("A", "A")));
    }

    [Fact]
    public void MergeSubsetOrder_FilteredItemNotInFull_Throws()
    {
        Assert.Throws<Utils.SubsetMismatchException>(
            () => Utils.MergeSubsetOrder(L("A", "B"), L("A", "Z"), L("Z", "A")));
    }

    [Fact]
    public void MergeSubsetOrder_DuplicateFilteredItem_Throws()
    {
        Assert.Throws<Utils.SubsetMismatchException>(
            () => Utils.MergeSubsetOrder(L("A", "B", "C"), L("A", "A"), L("A", "A")));
    }

    // ---------- RestoreOrder ----------

    [Fact]
    public void RestoreOrder_RestoresSnapshotOrder()
    {
        var target = new ObservableCollection<string> { "C", "A", "B" };

        Utils.RestoreOrder(target, L("A", "B", "C"));

        Assert.Equal(L("A", "B", "C"), target);
    }

    [Fact]
    public void RestoreOrder_AlreadyInOrder_KeepsContent()
    {
        var target = new ObservableCollection<string> { "A", "B", "C" };

        Utils.RestoreOrder(target, L("A", "B", "C"));

        Assert.Equal(L("A", "B", "C"), target);
    }

    [Fact]
    public void RestoreOrder_CountMismatch_DoesNothing()
    {
        var target = new ObservableCollection<string> { "B", "A" };

        Utils.RestoreOrder(target, L("A", "B", "C"));

        Assert.Equal(L("B", "A"), target);
    }

    [Fact]
    public void RestoreOrder_SnapshotItemMissing_DoesNotThrow()
    {
        var target = new ObservableCollection<string> { "B", "A" };

        Utils.RestoreOrder(target, L("A", "Z"));

        Assert.Equal(2, target.Count);
    }

    [Fact]
    public void RestoreOrder_UsesMoveNotReset()
    {
        // 必须用 Move 逐项还原：Clear+Add 会让 ListView/GridView 容器整体重建（丢滚动位置/选中态）
        var target = new ObservableCollection<string> { "C", "A", "B" };
        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => actions.Add(e.Action);

        Utils.RestoreOrder(target, L("A", "B", "C"));

        Assert.NotEmpty(actions);
        Assert.All(actions, a => Assert.Equal(NotifyCollectionChangedAction.Move, a));
    }

    [Fact]
    public void RestoreOrder_UnchangedOrder_EmitsNoEvents()
    {
        var target = new ObservableCollection<string> { "A", "B", "C" };
        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => actions.Add(e.Action);

        Utils.RestoreOrder(target, L("A", "B", "C"));

        Assert.Empty(actions);
    }
}
