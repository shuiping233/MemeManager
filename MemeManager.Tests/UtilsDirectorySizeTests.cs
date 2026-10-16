using MemeManager.Infrastructure;
using Xunit;

namespace MemeManager.Tests;

public class UtilsDirectorySizeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "MemeManagerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 测试清理失败不影响结论 */ }
    }

    // 递归求和：顶层 + 各级子目录的文件都要计入。
    [Fact]
    public void GetDirectorySize_SumsFilesInSubDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));
        File.WriteAllBytes(Path.Combine(_root, "top.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_root, "a", "mid.bin"), new byte[200]);
        File.WriteAllBytes(Path.Combine(_root, "a", "b", "deep.bin"), new byte[300]);

        Assert.Equal(600L, Utils.GetDirectorySize(_root));
    }

    // 目录不存在（缓存从未生成过）与空路径都返回 0，不抛异常。
    [Fact]
    public void GetDirectorySize_MissingOrEmptyPath_ReturnsZero()
    {
        Assert.Equal(0L, Utils.GetDirectorySize(_root));
        Assert.Equal(0L, Utils.GetDirectorySize(""));
    }
}
