using MemeManager.Infrastructure;
using MemeManager.Services;
using MemeManager.ViewModels;
using Xunit;

namespace MemeManager.Tests;

// 分类栏集合（CategoryList）的语义测试：它本身就是"当前视图"（= 全量分类名 ∩ 搜索关键词），
// 全量名单由 Page 从引擎灌入（SetAllCategoryNames）。
// 这里只验证 VM 的名单 / 视图维护逻辑（差分同步、实例复用、关键词过滤），不碰磁盘与引擎缓存。
public class MainViewModelCategoryFilterTests
{
    // 轻量构造：ConfigService / MemeDataEngine 的构造都不读盘，本文件的用例不触碰引擎。
    private static MainViewModel CreateViewModel()
    {
        var engine = new MemeDataEngine(new ConfigService());
        return new MainViewModel(engine, new SearchService(engine), new ClipboardService(), new CategoryService(engine));
    }

    // 用全量名单造分类（等价于 LoadCategories 从引擎 GetCategories() 灌入）。
    private static MainViewModel CreateViewModelWith(params string[] categoryNames)
    {
        var vm = CreateViewModel();
        vm.SetAllCategoryNames(categoryNames);
        return vm;
    }

    private static IEnumerable<string> ViewNames(MainViewModel vm) => vm.CategoryList.Select(c => c.Name);

    // ---------- SetAllCategoryNames / ApplyCategoryFilter ----------

    [Fact]
    public void SetAllCategoryNames_NoKeyword_ShowsAllInGivenOrder()
    {
        var vm = CreateViewModelWith("Anime", "Cats", "Dogs");

        Assert.Equal(new[] { "Anime", "Cats", "Dogs" }, ViewNames(vm));
        Assert.Equal(new[] { "Anime", "Cats", "Dogs" }, vm.AllCategoryNames);
    }

    [Fact]
    public void ApplyCategoryFilter_EmptyKeyword_ShowsAllCategories()
    {
        var vm = CreateViewModelWith("Anime", "Cats", "Dogs");

        vm.ApplyCategoryFilter("");

        Assert.Equal(3, vm.CategoryList.Count);
        Assert.Equal(string.Empty, vm.CategoryFilterKeyword);
    }

    [Fact]
    public void ApplyCategoryFilter_WhitespaceKeyword_TreatedAsNoFilter()
    {
        var vm = CreateViewModelWith("Anime", "Cats");

        vm.ApplyCategoryFilter("   ");

        Assert.Equal(2, vm.CategoryList.Count);
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
    public void ApplyCategoryFilter_NoMatch_LeavesViewEmptyWithoutTouchingFullNames()
    {
        var vm = CreateViewModelWith("Anime", "Cats");

        vm.ApplyCategoryFilter("zzz");

        Assert.Empty(vm.CategoryList);
        Assert.Equal(2, vm.AllCategoryNames.Count);
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
    public void ApplyCategoryFilter_KeepsFullListOrder()
    {
        var vm = CreateViewModelWith("Dogs", "Anime", "Ducks", "Cats");

        vm.ApplyCategoryFilter("d");

        // 视图顺序必须跟随全量名单顺序，而不是匹配命中顺序
        Assert.Equal(new[] { "Dogs", "Ducks" }, ViewNames(vm));
    }

    [Fact]
    public void ApplyCategoryFilter_ClearingKeyword_RestoresFullListWithSameInstances()
    {
        var vm = CreateViewModelWith("Anime", "Cats", "Dogs");
        var anime = vm.CategoryList[0];

        vm.ApplyCategoryFilter("cat");
        Assert.Equal(new[] { "Cats" }, ViewNames(vm));
        Assert.Equal(new[] { "Anime", "Cats", "Dogs" }, vm.AllCategoryNames);

        vm.ApplyCategoryFilter(null);

        Assert.Equal(new[] { "Anime", "Cats", "Dogs" }, ViewNames(vm));
        // 差分同步的意义：消失再出现的分类复用同一 VM 实例（ListView 容器可复用）
        Assert.Same(anime, vm.CategoryList[0]);
    }

    [Fact]
    public void ApplyCategoryFilter_Narrowing_KeepsRemainingInstance()
    {
        var vm = CreateViewModelWith("Anime", "Cats", "Dogs");
        var cats = vm.CategoryList[1];

        vm.ApplyCategoryFilter("s"); // Cats、Dogs 命中

        Assert.Equal(new[] { "Cats", "Dogs" }, ViewNames(vm));
        Assert.Same(cats, vm.CategoryList[0]);
    }

    // ---------- InsertCategory（新建分类） ----------

    [Fact]
    public void InsertCategory_NoFilter_AppearsInView()
    {
        var vm = CreateViewModelWith("Anime");

        var created = vm.InsertCategory("Cats");

        Assert.Contains(created, vm.CategoryList);
        Assert.Equal(new[] { "Anime", "Cats" }, ViewNames(vm));
        Assert.Equal(new[] { "Anime", "Cats" }, vm.AllCategoryNames);
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
    public void InsertCategory_NotMatchingKeyword_StaysInFullNamesOnly()
    {
        var vm = CreateViewModelWith("Anime");
        vm.ApplyCategoryFilter("cat");

        vm.InsertCategory("Dogs");

        Assert.Empty(vm.CategoryList);
        Assert.Equal(new[] { "Anime", "Dogs" }, vm.AllCategoryNames); // 全量名单仍包含新分类

        vm.ApplyCategoryFilter(null);
        Assert.Equal(new[] { "Anime", "Dogs" }, ViewNames(vm));
    }

    [Fact]
    public void InsertCategory_DuplicateName_DoesNotAppearTwice()
    {
        var vm = CreateViewModelWith("Cats");

        vm.InsertCategory("cats");

        Assert.Single(vm.CategoryList);
        Assert.Single(vm.AllCategoryNames);
    }

    // ---------- RemoveCategoryName（删除分类） ----------

    [Fact]
    public void RemoveCategoryName_RemovesFromViewAndFullNames()
    {
        var vm = CreateViewModelWith("Cats", "Dogs");

        vm.RemoveCategoryName("Cats");

        Assert.Equal(new[] { "Dogs" }, ViewNames(vm));
        Assert.Equal(new[] { "Dogs" }, vm.AllCategoryNames);
    }

    [Fact]
    public void RemoveCategoryName_HiddenByFilter_StillRemovedFromFullNames()
    {
        var vm = CreateViewModelWith("Cats", "Dogs");
        vm.ApplyCategoryFilter("a"); // 仅 Cats 命中，Dogs 在视图外

        vm.RemoveCategoryName("Dogs");

        Assert.Equal(new[] { "Cats" }, ViewNames(vm));
        Assert.Equal(new[] { "Cats" }, vm.AllCategoryNames);
    }

    // ---------- RenameCategoryName（改名） ----------

    [Fact]
    public void RenameCategoryName_BecomesMatching_InsertsInFullListOrder()
    {
        var vm = CreateViewModelWith("Anime", "Cats", "Dogs");
        vm.ApplyCategoryFilter("o"); // 仅 Dogs 命中
        Assert.Equal(new[] { "Dogs" }, ViewNames(vm));

        vm.RenameCategoryName("Cats", "Collie"); // 改名后含 "o"

        // 插入位置必须按全量名单顺序（Collie 在 Dogs 之前）
        Assert.Equal(new[] { "Collie", "Dogs" }, ViewNames(vm));
        Assert.Equal(new[] { "Anime", "Collie", "Dogs" }, vm.AllCategoryNames);
    }

    [Fact]
    public void RenameCategoryName_NoLongerMatching_IsRemovedFromView()
    {
        var vm = CreateViewModelWith("Cats", "Dogs");
        vm.ApplyCategoryFilter("a"); // 仅 Cats 命中
        Assert.Equal(new[] { "Cats" }, ViewNames(vm));

        vm.RenameCategoryName("Cats", "Kitty");

        Assert.Empty(vm.CategoryList);
        Assert.Equal(new[] { "Kitty", "Dogs" }, vm.AllCategoryNames); // 名单里位置不变
    }

    [Fact]
    public void RenameCategoryName_WithoutFilter_KeepsItemVisibleAndSameInstance()
    {
        var vm = CreateViewModelWith("Cats");
        var cats = vm.CategoryList[0];

        vm.RenameCategoryName("Cats", "Kitty");

        Assert.Equal(new[] { "Kitty" }, ViewNames(vm));
        Assert.Same(cats, vm.CategoryList[0]);
        Assert.Equal(new[] { "Kitty" }, vm.AllCategoryNames);
    }

    [Fact]
    public void RenameCategoryName_StillMatching_DoesNotDuplicate()
    {
        var vm = CreateViewModelWith("Cats", "Dogs");
        vm.ApplyCategoryFilter("a");

        vm.RenameCategoryName("Cats", "Catss"); // 仍含 "a"

        Assert.Equal(new[] { "Catss" }, ViewNames(vm));
    }

    // ---------- SetCategoryNameOrder（拖拽重排写回） ----------

    [Fact]
    public void SetCategoryNameOrder_UpdatesFullOrderAndView()
    {
        var vm = CreateViewModelWith("Anime", "Cats", "Dogs");

        vm.SetCategoryNameOrder(new[] { "Dogs", "Anime", "Cats" });

        Assert.Equal(new[] { "Dogs", "Anime", "Cats" }, vm.AllCategoryNames);
        Assert.Equal(new[] { "Dogs", "Anime", "Cats" }, ViewNames(vm));
    }

    [Fact]
    public void SetCategoryNameOrder_UnderFilter_ViewFollowsNewFullOrder()
    {
        var vm = CreateViewModelWith("Dogs", "Anime", "Ducks", "Cats");
        vm.ApplyCategoryFilter("d"); // 视图 = Dogs, Ducks（Cat 在视图外）
        Assert.Equal(new[] { "Dogs", "Ducks" }, ViewNames(vm));

        // 搜索态下拖拽：未显示项（Anime / Cats）保持原位，可见项换成新相对顺序
        vm.SetCategoryNameOrder(new[] { "Ducks", "Anime", "Dogs", "Cats" });

        Assert.Equal(new[] { "Ducks", "Anime", "Dogs", "Cats" }, vm.AllCategoryNames);
        Assert.Equal(new[] { "Ducks", "Dogs" }, ViewNames(vm));

        vm.ApplyCategoryFilter(null);
        Assert.Equal(new[] { "Ducks", "Anime", "Dogs", "Cats" }, ViewNames(vm));
    }

    // ---------- SetAllCategoryNames（引擎刷新） ----------

    [Fact]
    public void SetAllCategoryNames_ReplacingNames_DropsStaleInstancesKeepsReused()
    {
        var vm = CreateViewModelWith("Cats", "Dogs");
        var cats = vm.CategoryList[0];

        // 模拟引擎重载：Dogs 消失、Birds 新增（Cats 仍在）
        vm.SetAllCategoryNames(new[] { "Cats", "Birds" });

        Assert.Equal(new[] { "Cats", "Birds" }, ViewNames(vm));
        Assert.Same(cats, vm.CategoryList[0]);
    }

    [Fact]
    public void SetAllCategoryNames_WhileFiltered_AppliesKeywordToNewNames()
    {
        var vm = CreateViewModelWith("Cats", "Dogs");
        vm.ApplyCategoryFilter("do");

        vm.SetAllCategoryNames(new[] { "Dogs", "Cats", "Dondurma" });

        Assert.Equal(new[] { "Dogs", "Dondurma" }, ViewNames(vm));
    }
}
