using MemeManager.Infrastructure;
using Xunit;

namespace MemeManager.Tests;

/// <summary>
/// 后台回收策略的**纯函数**部分：启动判定与参数映射。
/// 真实回收动作（GC / EmptyWorkingSet / 计时）依赖进程状态与线程，不做单测。
/// </summary>
public class BackgroundMemoryReclaimerTests
{
    private static readonly BackgroundMemoryOptions Configured = new(TimeSpan.FromSeconds(5));

    [Fact]
    public void ShouldStart_True_WhenToggleOnAndDelayConfigured()
        => Assert.True(BackgroundMemoryReclaimer.ShouldStart(aggressiveEnabled: true, Configured));

    [Fact]
    public void ShouldStart_False_WhenToggleOff()
        => Assert.False(BackgroundMemoryReclaimer.ShouldStart(aggressiveEnabled: false, Configured));

    [Theory]
    [InlineData(0)]    // 0 = 禁用
    [InlineData(-1)]   // 负值 = 禁用
    public void ShouldStart_False_WhenTrimDelayNotPositive(int seconds)
        => Assert.False(BackgroundMemoryReclaimer.ShouldStart(
            aggressiveEnabled: true,
            new BackgroundMemoryOptions(TimeSpan.FromSeconds(seconds))));

    [Fact]
    public void FromAppConstants_MirrorsAppConstants()
        => Assert.Equal(
            AppConstants.WorkingSetTrimDelay,
            BackgroundMemoryOptions.FromAppConstants().FirstTrimDelay);
}
