using MemeManager.Infrastructure;
using MemeManager.Services;
using MemeManager.ViewModels;
using Xunit;

namespace MemeManager.Tests;

// 分类搜索"过滤视图"（分类栏的 ItemsSource）与数据源 CategoryList 的一致性。
// 分类栏绑定的是 FilteredCategoryList，因此增 / 删 / 改名都必须同步它，且过滤只作用于普通分类栏。
public class MainViewModelCategoryFilterTests
{
    // 轻量构造：ConfigService / MemeDataEngine 的构造都不读盘，VM 的过滤逻辑不触碰引擎。
    private static MainViewModel CreateViewModel()
    {
        var engine = new MemeDataEngine(new ConfigService());
        return new MainViewModel(engine, new SearchService(engine), new ClipboardService(), new CategoryService(engine));
    }

    private static MainViewModel CreateViewModelWith(params string[] categoryNames)
    {
        var vm = CreateViewModel();
        foreach (var name in categoryNames)
            vm.InsertCategory(name);
        return vm;
    }

    private static IEnumerable<string> ViewNames(MainViewModel vm) => vm.FilteredCategoryList.Select(c => c.Name);

    // ---------- ApplyCategoryFilter ----------

    [Fact]
    public void ApplyCategoryFilter_EmptyKeyword_ShowsAllCategories()
    {
        var vm = CreateViewModelWith("Anime", "Cats", "Dogs");

        vm.ApplyCategoryFilter("");

        Assert.Equal(3, vm.FilteredCategoryList.Count);
        Assert.Equal(vm.CategoryList, vm.FilteredCategoryList);
    }

    [Fact]
    public void ApplyCategoryFilter_WhitespaceKeyword_TreatedAsNoFilter()
    {
        var vm = CreateViewModelWith("Anime", "Cats");

        vm.ApplyCategoryFilter("   ");

        Assert.Equal(2, vm.FilteredCategoryList.Count);
        Assert.Equal(string.Empty, vm.CategoryFilterKeyword);
    }

    [Fact]
    public void ApplyCategoryFilter_FuzzyMatchesCaseInsensitively()
    {
        var vm = CreateViewModelWith("Anime", "Cats", "Dogs");

        vm.ApplyCategoryFilter("a");

        // Anime（开头 A）与 Cats（含 a）命中；Dogs 不含 a
        Assert.Equal(new[] { "Anime", "Cats" }, ViewNames(vm));
    }

    [Fact]
    public void ApplyCategoryFilter_TrimsKeyword()
    {
        var vm = CreateViewModelWith("Anime", "Cats");

        vm.ApplyCategoryFilter("  cats  ");

        Assert.Equal("cats", vm.CategoryFilterKeyword);
        Assert.Equal(new[] { "Cats" }, ViewNames(vm));
    }

    [Fact]
    public void ApplyCategoryFilter_NoMatch_LeavesViewEmptyWithoutTouchingSource()
    {
        var vm = CreateViewModelWith("Anime", "Cats");

        vm.ApplyCategoryFilter("zzz");

        Assert.Empty(vm.FilteredCategoryList);
        Assert.Equal(2, vm.CategoryList.Count);
    }

    [Fact]
    public void ApplyCategoryFilter_RepeatedCalls_DoNotDuplicate()
    {
        var vm = CreateViewModelWith("Cats", "Dogs");

        vm.ApplyCategoryFilter("a");
        vm.ApplyCategoryFilter("a");

        Assert.Equal(new[] { "Cats" }, ViewNames(vm));
    }

    [Fact]
    public void ApplyCategoryFilter_KeepsSourceOrder()
    {
        var vm = CreateViewModelWith("Dogs", "Anime", "Ducks", "Cats");

        vm.ApplyCategoryFilter("d");

        // 过滤结果顺序必须跟随数据源顺序，而不是匹配命中顺序
        Assert.Equal(new[] { "Dogs", "Ducks" }, ViewNames(vm));
    }

    [Fact]
    public void FilteredView_IsSubsetOfSource_InSourceOrder()
    {
        var vm = CreateViewModelWith("Dogs", "Anime", "Ducks", "Cats");

        vm.ApplyCategoryFilter("d");

        var expected = vm.CategoryList
            .Where(c => c.Name.Contains("d", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name);
        Assert.Equal(expected, ViewNames(vm));
    }

    // ---------- InsertCategory（新建分类） ----------

    [Fact]
    public void InsertCategory_NoFilter_AppearsInView()
    {
        var vm = CreateViewModelWith("Anime");

        var created = vm.InsertCategory("Cats");

        Assert.Contains(created, vm.CategoryList);
        Assert.Contains(created, vm.FilteredCategoryList);
        Assert.Equal(new[] { "Anime", "Cats" }, ViewNames(vm));
    }

    [Fact]
    public void InsertCategory_MatchingKeyword_AppearsInView()
    {
        var vm = CreateViewModelWith("Anime");
        vm.ApplyCategoryFilter("cat");

        vm.InsertCategory("Cats");

        Assert.Equal(new[] { "Cats" }, ViewNames(vm));
    }

    [Fact]
    public void InsertCategory_NotMatchingKeyword_StaysHidden()
    {
        var vm = CreateViewModelWith("Anime");
        vm.ApplyCategoryFilter("cat");

        vm.InsertCategory("Dogs");

        Assert.Empty(vm.FilteredCategoryList);
        Assert.Equal(2, vm.CategoryList.Count); // 数据源仍包含新分类
    }

    [Fact]
    public void InsertCategory_ReturnsViewModelCarryingName()
    {
        var vm = CreateViewModel();

        var created = vm.InsertCategory("Cats", 7);

        Assert.Equal("Cats", created.Name);
        Assert.Equal(7, created.Count);
    }

    // ---------- 删除 / 改名同步 ----------

    [Fact]
    public void SyncFilteredOnRemove_RemovesFromView()
    {
        var vm = CreateViewModelWith("Cats", "Dogs");
        var cats = vm.CategoryList.First(c => c.Name == "Cats");

        vm.SyncFilteredOnRemove(cats);

        Assert.Equal(new[] { "Dogs" }, ViewNames(vm));
    }

    [Fact]
    public void SyncFilteredOnRename_BecomesMatching_InsertsInSourceOrder()
    {
        var vm = CreateViewModelWith("Anime", "Cats", "Dogs");
        vm.ApplyCategoryFilter("o"); // 仅 Dogs 命中
        Assert.Equal(new[] { "Dogs" }, ViewNames(vm));

        var cats = vm.CategoryList.First(c => c.Name == "Cats");
        cats.Name = "Collie"; // 改名后含 "o"
        vm.SyncFilteredOnRename(cats);

        // 插入位置必须按数据源顺序（Collie 在 Dogs 之前）
        Assert.Equal(new[] { "Collie", "Dogs" }, ViewNames(vm));
    }

    [Fact]
    public void SyncFilteredOnRename_NoLongerMatching_IsRemoved()
    {
        var vm = CreateViewModelWith("Cats", "Dogs");
        vm.ApplyCategoryFilter("a"); // 仅 Cats 命中
        Assert.Equal(new[] { "Cats" }, ViewNames(vm));

        var cats = vm.CategoryList.First(c => c.Name == "Cats");
        cats.Name = "Kitty";
        vm.SyncFilteredOnRename(cats);

        Assert.Empty(vm.FilteredCategoryList);
    }

    [Fact]
    public void SyncFilteredOnRename_WithoutFilter_KeepsItemVisible()
    {
        var vm = CreateViewModelWith("Cats");
        var cats = vm.CategoryList[0];

        cats.Name = "Kitty";
        vm.SyncFilteredOnRename(cats);

        Assert.Equal(new[] { "Kitty" }, ViewNames(vm));
    }

    [Fact]
    public void SyncFilteredOnRename_StillMatching_DoesNotDuplicate()
    {
        var vm = CreateViewModelWith("Cats", "Dogs");
        vm.ApplyCategoryFilter("a");

        var cats = vm.CategoryList.First(c => c.Name == "Cats");
        cats.Name = "Catss"; // 仍含 "a"
        vm.SyncFilteredOnRename(cats);

        Assert.Equal(new[] { "Catss" }, ViewNames(vm));
    }

    [Fact]
    public void ApplyCategoryFilter_AfterSourceRebuilt_RebuildsViewWithNewInstances()
    {
        // 模拟 RebuildStrategy：数据源被整体重建（旧 VM 引用被全部丢弃）
        var vm = CreateViewModelWith("Cats", "Dogs");
        vm.ApplyCategoryFilter("a");
        var oldItem = vm.FilteredCategoryList[0];

        vm.CategoryList.Clear();
        vm.CategoryList.Add(new CategoryViewModel("Cats", 0));
        vm.CategoryList.Add(new CategoryViewModel("Birds", 0));
        vm.ApplyCategoryFilter(vm.CategoryFilterKeyword);

        Assert.Equal(new[] { "Cats" }, ViewNames(vm));
        Assert.NotSame(oldItem, vm.FilteredCategoryList[0]);
    }
}
