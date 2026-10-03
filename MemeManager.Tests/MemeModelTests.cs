using MemeManager.Infrastructure;
using MemeManager.Models;
using Xunit;

namespace MemeManager.Tests;

/// <summary>
/// <see cref="MemeModel"/> 的搜索/分类谓词（纯函数）。
/// 这两条谓词同时被引擎查询（MemeDataEngine.GetMemes）与 UI 增量插入
/// （MainPage.InsertMemesView）复用，故“空关键词返回 false”是必须钉住的不变量：
/// 它表示“不匹配任何项”，调用方**必须**自行先判空，否则非搜索态会被整批滤掉。
/// </summary>
public class MemeModelTests
{
    private static MemeModel Make(string title = "猫猫", string category = "Cat", string[]? tags = null)
        => new() { Title = title, Category = category, Tags = tags is null ? [] : [.. tags] };

    // ---------- HasKeyword ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasKeyword_BlankKeyword_ReturnsFalse_RegardlessOfContent(string? keyword)
    {
        var meme = Make(title: "猫猫", tags: ["沙雕"]);

        Assert.False(meme.HasKeyword(keyword));
    }

    [Fact]
    public void HasKeyword_MatchesTitle_CaseInsensitively()
    {
        var meme = Make(title: "Funny Cat");

        Assert.True(meme.HasKeyword("funny"));
        Assert.True(meme.HasKeyword("CAT"));
        Assert.True(meme.HasKeyword("Funny Cat"));
    }

    [Fact]
    public void HasKeyword_MatchesTag_WhenTitleMisses()
    {
        var meme = Make(title: "无关标题", tags: ["沙雕", "熊猫头"]);

        Assert.True(meme.HasKeyword("熊猫"));
        Assert.True(meme.HasKeyword("沙雕"));
    }

    [Fact]
    public void HasKeyword_ReturnsFalse_WhenNeitherTitleNorAnyTagMatches()
    {
        var meme = Make(title: "猫猫", tags: ["沙雕"]);

        Assert.False(meme.HasKeyword("狗"));
    }

    [Fact]
    public void HasKeyword_WithoutTags_StillMatchesTitle()
    {
        var meme = Make(title: "Only Title");

        Assert.True(meme.HasKeyword("only"));
        Assert.False(meme.HasKeyword("zzz"));
    }

    // ---------- MatchesCategory ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MatchesCategory_BlankCategory_ReturnsFalse(string? category)
    {
        Assert.False(Make(category: "Cat").MatchesCategory(category));
    }

    [Theory]
    [InlineData("Cat", true)]
    [InlineData("cat", true)]
    [InlineData("CAT", true)]
    [InlineData("Cat2", false)]
    [InlineData("Ca", false)]
    [InlineData("未分类", false)]
    public void MatchesCategory_IsExactButCaseInsensitive(string category, bool expected)
    {
        Assert.Equal(expected, Make(category: "Cat").MatchesCategory(category));
    }
}

/// <summary>
/// <see cref="ImportResult"/> 的不变量。
/// UI 侧靠 <c>Added.Count &gt; 0</c> 决定“增量插入”还是“退回全量刷新”，
/// 而 <see cref="ImportResult.Empty"/> 是并发被守卫拒绝时的返回值，必须始终可用（Added 非 null）。
/// </summary>
public class ImportResultTests
{
    [Fact]
    public void Empty_CarriesNoData_AndAddedIsNeverNull()
    {
        var empty = ImportResult.Empty;

        Assert.Equal(0, empty.Imported);
        Assert.Equal(0, empty.Duplicate);
        Assert.Null(empty.DuplicateModel);
        Assert.NotNull(empty.Added);
        Assert.Empty(empty.Added);
    }

    [Fact]
    public void Constructor_PreservesFieldsAndModelOrder()
    {
        var first = new MemeModel { Hash = "aaa", Priority = 1 };
        var second = new MemeModel { Hash = "bbb", Priority = 2 };

        var result = new ImportResult(2, 1, second, new[] { first, second });

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Duplicate);
        Assert.Same(second, result.DuplicateModel);
        Assert.Equal(new[] { first, second }, result.Added);
    }
}
