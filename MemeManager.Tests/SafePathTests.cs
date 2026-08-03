using MemeManager.Infrastructure;
using Xunit;

namespace MemeManager.Tests;

public class SafePathTests
{
    // ---------- CombineChildPath ----------

    [Fact]
    public void CombineChildPath_NormalChild_ReturnsJoinedPath()
    {
        var result = SafePath.CombineChildPath(@"C:\Meme", "Cats");
        Assert.Equal(@"C:\Meme\Cats", result);
    }

    [Fact]
    public void CombineChildPath_ChildSubPath_ReturnsJoinedPath()
    {
        var result = SafePath.CombineChildPath(@"C:\Meme", @"Cats\sub");
        Assert.Equal(@"C:\Meme\Cats\sub", result);
    }

    [Fact]
    public void CombineChildPath_DoubleDotEscape_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", ".."));
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", @"..\..\secret"));
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", @"Cats\..\..\secret"));
    }

    [Fact]
    public void CombineChildPath_AbsoluteChild_Throws()
    {
        // Path.Combine 遇绝对路径 child 直接返回 child，必须被边界拦截
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", @"D:\evil"));
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", @"C:\Windows\System32"));
    }

    [Fact]
    public void CombineChildPath_UncChild_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", @"\\server\share\evil"));
    }

    [Fact]
    public void CombineChildPath_SeparatorPrefixedChild_Throws()
    {
        // "\evil" 会被 Path.Combine 当作根相对路径解析，落到 C:\evil 而非 C:\Meme\evil
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", @"\evil"));
    }

    [Fact]
    public void CombineChildPath_DotItself_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", "."));
    }

    [Fact]
    public void CombineChildPath_ParentTrailingSlash_ReturnsJoinedPath()
    {
        var result = SafePath.CombineChildPath(@"C:\Meme\", "Cats");
        Assert.Equal(@"C:\Meme\Cats", result);
    }

    // ---------- IsSubPathOf ----------

    [Fact]
    public void IsSubPathOf_NormalChild_ReturnsTrue()
    {
        Assert.True(SafePath.IsSubPathOf(@"C:\Meme", @"C:\Meme\Cats"));
        Assert.True(SafePath.IsSubPathOf(@"C:\Meme", @"C:\Meme\Cats\a.png"));
    }

    [Fact]
    public void IsSubPathOf_EqualToBase_ReturnsFalse()
    {
        Assert.False(SafePath.IsSubPathOf(@"C:\Meme", @"C:\Meme"));
    }

    [Fact]
    public void IsSubPathOf_DoubleDotEscape_ReturnsFalse()
    {
        Assert.False(SafePath.IsSubPathOf(@"C:\Meme", @"C:\Meme\..\secret"));
        Assert.False(SafePath.IsSubPathOf(@"C:\Meme", @"C:\secret"));
    }

    [Fact]
    public void IsSubPathOf_CrossDrive_ReturnsFalse()
    {
        Assert.False(SafePath.IsSubPathOf(@"C:\Meme", @"D:\Meme\a.png"));
    }

    [Fact]
    public void IsSubPathOf_DotPrefixedFileName_NotFalsePositive()
    {
        // relative = "..hidden"，以 ".." 开头但并非逃逸（逃逸是 ".." 或 "..\"）
        Assert.True(SafePath.IsSubPathOf(@"C:\Meme", @"C:\Meme\..hidden"));
        Assert.True(SafePath.IsSubPathOf(@"C:\Meme", @"C:\Meme\hello..world.png"));
    }

    [Fact]
    public void IsSubPathOf_SimilarPrefixDir_NotFalsePositive()
    {
        // C:\MemeBackup 不是 C:\Meme 的子路径（防字符串前缀误判）
        Assert.False(SafePath.IsSubPathOf(@"C:\Meme", @"C:\MemeBackup\a.png"));
    }

    [Fact]
    public void IsSubPathOf_TrailingSlash_NotFalsePositive()
    {
        Assert.True(SafePath.IsSubPathOf(@"C:\Meme\", @"C:\Meme\Cats\"));
        Assert.False(SafePath.IsSubPathOf(@"C:\Meme\", @"C:\MemeBackup\a.png"));
    }

    [Fact]
    public void IsSubPathOf_CaseInsensitive_ReturnsTrue()
    {
        Assert.True(SafePath.IsSubPathOf(@"C:\Meme", @"c:\meme\Cats"));
    }

    [Fact]
    public void IsSubPathOf_RootDirectory_Works()
    {
        Assert.True(SafePath.IsSubPathOf(@"C:\", @"C:\Windows\System32"));
        Assert.False(SafePath.IsSubPathOf(@"C:\", @"D:\Windows"));
    }
}
