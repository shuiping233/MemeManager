using MemeManager.Infrastructure;
using Xunit;

namespace MemeManager.Tests;

public class VersionStringTests
{
    [Theory]
    [InlineData("v1.2.3")]
    [InlineData("V1.2.3")]
    [InlineData("1.2.3")]
    [InlineData("1.2")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3-beta.1")]
    public void TryParse_ValidVersions_ReturnsTrue(string s)
    {
        Assert.True(VersionString.TryParse(s, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("v1.2.x")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.")]
    [InlineData("999999999999999999999999")]
    [InlineData("2026-07-14 12:00:00 dev build")]
    public void TryParse_InvalidVersions_ReturnsFalse(string? s)
    {
        Assert.False(VersionString.TryParse(s, out _));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.2.10", "1.2.9", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("1.2.3-beta", "1.2.3", -1)]
    [InlineData("1.2.3", "1.2.3-beta", 1)]
    [InlineData("1.2.3-alpha", "1.2.3-beta", -1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    public void Compare_OrdersVersions(string a, string b, int expected)
    {
        Assert.True(VersionString.TryParse(a, out var va));
        Assert.True(VersionString.TryParse(b, out var vb));
        Assert.Equal(expected, Math.Sign(va.CompareTo(vb)));
    }

    [Theory]
    [InlineData("v1.2.4", "v1.2.3", true)]
    [InlineData("v1.2.2", "v1.2.3", false)]
    [InlineData("v1.2.3", "v1.2.3", false)]
    [InlineData("v1.2.3", "dev build", false)]   // 当前版本无法解析 → 不提示
    [InlineData("dev build", "v1.2.3", false)]   // 远端版本无法解析 → 不提示
    public void IsNewer_Works(string? latest, string? current, bool expected)
    {
        Assert.Equal(expected, VersionString.IsNewer(latest, current));
    }

    [Theory]
    [InlineData("2026-07-14 12:00:00 dev build", true)]   // 日期里的 '-' 会被 TryParse 误解析,必须靠后缀识别
    [InlineData("2026.08.14.1234 dev build", true)]       // 带点的 dev 版本
    [InlineData("dev build", true)]
    [InlineData("DEV BUILD", true)]                       // 大小写不敏感
    [InlineData("2026-07-14 12:00:00", false)]            // 无 dev build 字样
    [InlineData("v1.12.12", false)]
    [InlineData("1.2.3-beta.1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDevBuild_Works(string? version, bool expected)
    {
        Assert.Equal(expected, VersionString.IsDevBuild(version));
    }
}
