using MemeManager.Infrastructure;
using Xunit;

namespace MemeManager.Tests;

/// <summary>
/// 内存诊断的**纯函数**部分（MB 格式 / 差值格式 / 日志行拼装）。
/// 采集本身（Process / GC / BitmapImage 计数）属运行期状态，不做单测。
/// </summary>
public class MemoryDiagnosticsTests
{
    private const long MB = 1024 * 1024;

    [Theory]
    [InlineData(0, "0.0MB")]
    [InlineData(MB, "1.0MB")]
    [InlineData(-MB, "-1.0MB")]
    [InlineData(1536 * 1024, "1.5MB")]
    [InlineData(271 * MB, "271.0MB")]
    public void FormatMB_UsesOneDecimalAndHandlesNegative(long bytes, string expected)
        => Assert.Equal(expected, MemoryDiagnostics.FormatMB(bytes));

    [Theory]
    [InlineData(200, 100, "-100.0MB (-50.0%)")]
    [InlineData(100, 200, "100.0MB (100.0%)")]
    [InlineData(100, 100, "0.0MB (0.0%)")]
    public void FormatDelta_ShowsSignedDeltaAndPercent(int beforeMb, int afterMb, string expected)
        => Assert.Equal(expected, MemoryDiagnostics.FormatDelta(beforeMb * MB, afterMb * MB));

    [Fact]
    public void FormatDelta_ZeroBefore_FallsBackToNaInsteadOfDividingByZero()
        => Assert.Equal("5.0MB (n/a)", MemoryDiagnostics.FormatDelta(0, 5 * MB));

    [Fact]
    public void ToLogLine_IncludesTagAndCoreMetrics()
    {
        var snapshot = new MemorySnapshot(3 * MB, 271 * MB, 268 * MB, LiveBitmapImages: -1, PageAlive: null);

        Assert.Equal(
            "[Memory] BeforeHide: Managed=3.0MB Private=271.0MB WorkingSet=268.0MB",
            snapshot.ToLogLine("BeforeHide"));
    }

    [Fact]
    public void ToLogLine_AppendsOptionalFieldsOnlyWhenProvided()
    {
        var withOptionals = new MemorySnapshot(MB, 2 * MB, 3 * MB, LiveBitmapImages: 7, PageAlive: false);
        Assert.Equal(
            "[Memory] AfterTeardown: Managed=1.0MB Private=2.0MB WorkingSet=3.0MB LiveBitmapImages=7 PageAlive=False",
            withOptionals.ToLogLine("AfterTeardown"));

        var withoutOptionals = new MemorySnapshot(MB, 2 * MB, 3 * MB, LiveBitmapImages: -1, PageAlive: null);
        string line = withoutOptionals.ToLogLine("AfterTeardown");
        Assert.DoesNotContain("LiveBitmapImages", line);
        Assert.DoesNotContain("PageAlive", line);
    }

    [Fact]
    public void ToLogLine_PageAliveTrue_IsNotConfusedWithAbsentProbe()
    {
        var alive = new MemorySnapshot(MB, MB, MB, LiveBitmapImages: 0, PageAlive: true);

        Assert.Contains("PageAlive=True", alive.ToLogLine("AfterTeardown"));
    }

    [Fact]
    public void ShouldTrimWorkingSet_True_OnlyWhenHiddenAndNotClosing()
        => Assert.True(MemoryDiagnostics.ShouldTrimWorkingSet(
            TimeSpan.FromSeconds(30), isVisible: false, isClosing: false));

    [Theory]
    [InlineData(0)]   // 0 = 显式禁用
    [InlineData(-1)]  // 负值 = 禁用
    public void ShouldTrimWorkingSet_False_WhenDelayDisabledOrMissing(int seconds)
        => Assert.False(MemoryDiagnostics.ShouldTrimWorkingSet(
            TimeSpan.FromSeconds(seconds), isVisible: false, isClosing: false));

    [Theory]
    [InlineData(true, false)]   // 窗口可见：裁剪会让用户立刻感到缺页卡顿
    [InlineData(false, true)]   // 退出流程中
    [InlineData(true, true)]
    public void ShouldTrimWorkingSet_False_WhenVisibleOrClosing(bool isVisible, bool isClosing)
        => Assert.False(MemoryDiagnostics.ShouldTrimWorkingSet(
            TimeSpan.FromSeconds(30), isVisible, isClosing));
}
