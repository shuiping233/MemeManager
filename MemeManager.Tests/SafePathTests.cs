using MemeManager.Infrastructure;
using Xunit;

namespace MemeManager.Tests;

public class SafePathTests
{
    // ---------- CombineChildPath ----------

    [Fact]
    public void CombineChildPath_正常子路径_返回拼接结果()
    {
        var result = SafePath.CombineChildPath(@"C:\Meme", "Cats");
        Assert.Equal(@"C:\Meme\Cats", result);
    }

    [Fact]
    public void CombineChildPath_子目录路径_返回拼接结果()
    {
        var result = SafePath.CombineChildPath(@"C:\Meme", @"Cats\sub");
        Assert.Equal(@"C:\Meme\Cats\sub", result);
    }

    [Fact]
    public void CombineChildPath_双点逃逸_抛异常()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", ".."));
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", @"..\..\secret"));
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", @"Cats\..\..\secret"));
    }

    [Fact]
    public void CombineChildPath_绝对路径child_抛异常()
    {
        // Path.Combine 遇绝对路径 child 直接返回 child，必须被边界拦截
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", @"D:\evil"));
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", @"C:\Windows\System32"));
    }

    [Fact]
    public void CombineChildPath_UNC路径child_抛异常()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", @"\\server\share\evil"));
    }

    [Fact]
    public void CombineChildPath_以分隔符开头的child_抛异常()
    {
        // "\evil" 会被 Path.Combine 当作根相对路径解析，落到 C:\evil 而非 C:\Meme\evil
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", @"\evil"));
    }

    [Fact]
    public void CombineChildPath_点自身_抛异常()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SafePath.CombineChildPath(@"C:\Meme", "."));
    }

    [Fact]
    public void CombineChildPath_父目录尾斜杠_仍正常()
    {
        var result = SafePath.CombineChildPath(@"C:\Meme\", "Cats");
        Assert.Equal(@"C:\Meme\Cats", result);
    }

    // ---------- IsSubPathOf ----------

    [Fact]
    public void IsSubPathOf_正常子路径_返回true()
    {
        Assert.True(SafePath.IsSubPathOf(@"C:\Meme", @"C:\Meme\Cats"));
        Assert.True(SafePath.IsSubPathOf(@"C:\Meme", @"C:\Meme\Cats\a.png"));
    }

    [Fact]
    public void IsSubPathOf_等于base_返回false()
    {
        Assert.False(SafePath.IsSubPathOf(@"C:\Meme", @"C:\Meme"));
    }

    [Fact]
    public void IsSubPathOf_双点逃逸_返回false()
    {
        Assert.False(SafePath.IsSubPathOf(@"C:\Meme", @"C:\Meme\..\secret"));
        Assert.False(SafePath.IsSubPathOf(@"C:\Meme", @"C:\secret"));
    }

    [Fact]
    public void IsSubPathOf_跨盘_返回false()
    {
        Assert.False(SafePath.IsSubPathOf(@"C:\Meme", @"D:\Meme\a.png"));
    }

    [Fact]
    public void IsSubPathOf_点开头的合法文件名_不被误判为逃逸()
    {
        // relative = "..hidden"，以 ".." 开头但并非逃逸（逃逸是 ".." 或 "..\"）
        Assert.True(SafePath.IsSubPathOf(@"C:\Meme", @"C:\Meme\..hidden"));
        Assert.True(SafePath.IsSubPathOf(@"C:\Meme", @"C:\Meme\hello..world.png"));
    }

    [Fact]
    public void IsSubPathOf_前缀相似目录_不误判()
    {
        // C:\MemeBackup 不是 C:\Meme 的子路径（防字符串前缀误判）
        Assert.False(SafePath.IsSubPathOf(@"C:\Meme", @"C:\MemeBackup\a.png"));
    }

    [Fact]
    public void IsSubPathOf_尾斜杠_不误判()
    {
        Assert.True(SafePath.IsSubPathOf(@"C:\Meme\", @"C:\Meme\Cats\"));
        Assert.False(SafePath.IsSubPathOf(@"C:\Meme\", @"C:\MemeBackup\a.png"));
    }

    [Fact]
    public void IsSubPathOf_大小写不敏感_返回true()
    {
        Assert.True(SafePath.IsSubPathOf(@"C:\Meme", @"c:\meme\Cats"));
    }

    [Fact]
    public void IsSubPathOf_根目录_正常工作()
    {
        Assert.True(SafePath.IsSubPathOf(@"C:\", @"C:\Windows\System32"));
        Assert.False(SafePath.IsSubPathOf(@"C:\", @"D:\Windows"));
    }
}
