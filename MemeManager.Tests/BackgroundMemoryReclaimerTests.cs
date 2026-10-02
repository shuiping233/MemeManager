using MemeManager.Infrastructure;
using Xunit;

namespace MemeManager.Tests;

/// <summary>
/// 后台回收策略的**纯函数**部分：启动判定与参数映射。
/// 真实回收动作（GC / EmptyWorkingSet / 计时）依赖进程状态与线程，不做单测。
/// </summary>
public class BackgroundMemoryReclaimerTests
{
    private static readonly BackgroundMemoryOptions Configured = new(
        FirstTrimDelay: TimeSpan.FromSeconds(5),
        MaintenanceInterval: TimeSpan.FromSeconds(30),
        MaintenanceRounds: 10);

    [Fact]
    public void ShouldStart_True_WhenToggleOnAndActionsConfigured()
        => Assert.True(BackgroundMemoryReclaimer.ShouldStart(aggressiveEnabled: true, Configured));

    [Fact]
    public void ShouldStart_False_WhenToggleOff()
        => Assert.False(BackgroundMemoryReclaimer.ShouldStart(aggressiveEnabled: false, Configured));

    [Theory]
    [InlineData(0, 0)]     // 首次与周期都禁用 → 策略整体无效
    [InlineData(-1, 30)]   // 首次禁用，但仍有周期维护 → 见下一条用例（应为 true）
    [InlineData(5, -1)]    // 有首次回收、无周期维护
    public void ShouldStart_OnlyFalse_WhenBothActionsDisabled(int firstDelaySeconds, int intervalSeconds)
    {
        var options = new BackgroundMemoryOptions(
            TimeSpan.FromSeconds(firstDelaySeconds),
            TimeSpan.FromSeconds(intervalSeconds),
            MaintenanceRounds: 0);

        bool expected = firstDelaySeconds > 0 || intervalSeconds > 0;
        Assert.Equal(expected, BackgroundMemoryReclaimer.ShouldStart(true, options));
    }

    [Fact]
    public void FromAppConstants_MirrorsAppConstants()
    {
        var options = BackgroundMemoryOptions.FromAppConstants();

        Assert.Equal(AppConstants.WorkingSetTrimDelay, options.FirstTrimDelay);
        Assert.Equal(AppConstants.IdleMemoryProbeInterval, options.MaintenanceInterval);
        Assert.Equal(AppConstants.IdleMemoryProbeTimes, options.MaintenanceRounds);
    }
}
