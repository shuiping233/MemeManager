using MemeManager.Infrastructure;
using Xunit;

namespace MemeManager.Tests;

public class FileNameValidatorTests
{
    [Theory]
    [InlineData("Cats")]
    [InlineData("Anime")]
    [InlineData("2026")]
    [InlineData("中文分类")]
    [InlineData("emoji😀分类")]
    public void IsValidCategoryName_正常名称_返回true(string name)
    {
        Assert.True(FileNameValidator.IsValidCategoryName(name));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(".. ")]
    public void IsValidCategoryName_点类名称_返回false(string name)
    {
        Assert.False(FileNameValidator.IsValidCategoryName(name));
    }

    [Fact]
    public void IsValidCategoryName_前导空格_允许()
    {
        // Windows 保留前导空格（目录名与分类名一致），不应误杀；
        // 只有尾随空格/点会被吞掉导致不一致（见 IsValidCategoryName_尾随点或空格_返回false）
        Assert.True(FileNameValidator.IsValidCategoryName(" abc"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidCategoryName_空或空白_返回false(string name)
    {
        Assert.False(FileNameValidator.IsValidCategoryName(name));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData(@"..\..")]
    [InlineData("旅行/2026")]
    public void IsValidCategoryName_含路径分隔符_返回false(string name)
    {
        Assert.False(FileNameValidator.IsValidCategoryName(name));
    }

    [Theory]
    [InlineData("abc.")]
    [InlineData("abc ")]
    public void IsValidCategoryName_尾随点或空格_返回false(string name)
    {
        Assert.False(FileNameValidator.IsValidCategoryName(name));
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
        // 非安全边界：仅因 CreateDirectory("...CON") 会失败，提前拦截避免"内存有分类、磁盘没目录"脏状态
        Assert.False(FileNameValidator.IsValidCategoryName(name));
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
        Assert.False(FileNameValidator.IsValidCategoryName(name));
    }

    [Fact]
    public void IsValidCategoryName_点开头的隐藏风格名称_允许()
    {
        // ".gitignore" 这类点开头名称是合法 Windows 文件名，不应误杀
        Assert.True(FileNameValidator.IsValidCategoryName("..hidden"));
    }
}
