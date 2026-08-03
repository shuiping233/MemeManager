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

    // ---------- IsValidCategoryName ----------

    [Theory]
    [InlineData("Cats")]
    [InlineData("Anime")]
    [InlineData("2026")]
    [InlineData("中文分类")]
    [InlineData("emoji😀分类")]
    public void IsValidCategoryName_正常名称_返回true(string name)
    {
        Assert.True(SafePath.IsValidCategoryName(name));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(".. ")]
    public void IsValidCategoryName_点类名称_返回false(string name)
    {
        Assert.False(SafePath.IsValidCategoryName(name));
    }

    [Fact]
    public void IsValidCategoryName_前导空格_允许()
    {
        // Windows 保留前导空格（目录名与分类名一致），不应误杀；
        // 只有尾随空格/点会被吞掉导致不一致（见 IsValidCategoryName_尾随点或空格_返回false）
        Assert.True(SafePath.IsValidCategoryName(" abc"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidCategoryName_空或空白_返回false(string name)
    {
        Assert.False(SafePath.IsValidCategoryName(name));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData(@"..\..")]
    [InlineData("旅行/2026")]
    public void IsValidCategoryName_含路径分隔符_返回false(string name)
    {
        Assert.False(SafePath.IsValidCategoryName(name));
    }

    [Theory]
    [InlineData("abc.")]
    [InlineData("abc ")]
    public void IsValidCategoryName_尾随点或空格_返回false(string name)
    {
        Assert.False(SafePath.IsValidCategoryName(name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("PRN")]
    [InlineData("NUL")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("com9.log")]
    [InlineData("LPT3")]
    public void IsValidCategoryName_Windows保留设备名_返回false(string name)
    {
        Assert.False(SafePath.IsValidCategoryName(name));
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    public void IsValidCategoryName_非法字符_返回false(string name)
    {
        Assert.False(SafePath.IsValidCategoryName(name));
    }

    [Fact]
    public void IsValidCategoryName_点开头的隐藏风格名称_允许()
    {
        // ".gitignore" 这类点开头名称是合法 Windows 文件名，不应误杀
        Assert.True(SafePath.IsValidCategoryName("..hidden"));
    }
}
