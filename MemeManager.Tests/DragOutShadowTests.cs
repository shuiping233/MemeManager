using MemeManager.Infrastructure;
using Xunit;

namespace MemeManager.Tests;

// 拖出影子副本：副本落在 %TEMP% 下镜像分类结构的目录，交给外部目标的是副本而非数据目录里的源文件，
// 因此资源管理器的 Move 语义带不走源文件。
public class DragOutShadowTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "MemeManagerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 测试清理失败不影响结论 */ }
    }

    // 副本路径镜像"分类/文件名"，且文件名保持原样——MainPage.MemesFromDataViewAsync 的兜底
    // 按文件名反查 MemeModel，依赖这一点。
    [Fact]
    public void PathFor_MirrorsCategoryStructure()
    {
        var path = DragOutShadow.PathFor("catA", "abc123.jpg", _root);

        Assert.Equal(Path.Combine(_root, "catA", "abc123.jpg"), path);
        Assert.Equal("abc123.jpg", Path.GetFileName(path));
    }

    // 同一张图可能同时存在于两个分类（同名），分类层必须把它们分开，否则后写的会覆盖前一份。
    [Fact]
    public void PathFor_SameFileNameInDifferentCategories_StayApart()
    {
        var a = DragOutShadow.PathFor("catA", "same.jpg", _root);
        var b = DragOutShadow.PathFor("catB", "same.jpg", _root);

        Assert.NotEqual(a, b);
        Assert.Equal("same.jpg", Path.GetFileName(a));
        Assert.Equal("same.jpg", Path.GetFileName(b));
    }

    [Fact]
    public void CleanupStale_RemovesLeftoverCopies()
    {
        var leftover = DragOutShadow.PathFor("catA", "abc123.jpg", _root);
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "x");

        DragOutShadow.CleanupStale(_root);

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void CleanupStale_MissingRoot_DoesNotThrow()
    {
        DragOutShadow.CleanupStale(_root);

        Assert.False(Directory.Exists(_root));
    }
}
