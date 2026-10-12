using MemeManager.Services;

namespace MemeManager.Infrastructure;

/// <summary>
/// 后台内存回收策略的参数。默认取 <see cref="AppConstants"/>，也可单独注入以便测试或调参。
/// </summary>
/// <param name="FirstTrimDelay">窗口隐藏后，隔多久做这一次回收（延迟是为了避开"隐藏后马上呼出"）。</param>
public sealed record BackgroundMemoryOptions(TimeSpan FirstTrimDelay)
{
    public static BackgroundMemoryOptions FromAppConstants() => new(AppConstants.WorkingSetTrimDelay);
}

/// <summary>
/// <see cref="IBackgroundMemoryReclaimer"/> 的默认实现：
///
/// ```text
/// 窗口隐藏 → 等 FirstTrimDelay（默认 5s，避开"隐藏后马上呼出"）
///          → 强制压缩 GC（含终结器队列）+ EmptyWorkingSet    ← 只做这一次
/// 窗口可见 / 进程退出 → 取消（还没到点就什么都不做）
/// ```
///
/// **为什么延迟**：元素从视觉树摘除后，WinRT 侧引用是异步断开的——隐藏瞬间 GC 几乎回收不到东西；
/// 等几秒后再 GC + WaitForPendingFinalizers 才拿得到那几十 MB（实测 -47MB 全在 GC 这一步）。
/// EmptyWorkingSet 只压 Working Set、对 Private 零贡献，且效果会被后续访问自然回填。
///
/// **为什么只做一次**：早期试过"每隔 N 秒再压一次"的周期维护，结果是后台出现规律的缺页与工作集
/// 回填（观感上像"持续增长"），收益不值得，故取消。
///
/// 线程：在后台线程执行（GC 与工作集裁剪不需要 UI 线程），不阻塞 UI。
/// </summary>
public sealed class BackgroundMemoryReclaimer : IBackgroundMemoryReclaimer
{
    private readonly BackgroundMemoryOptions _options;
    private readonly ConfigService _config;

    private CancellationTokenSource? _cts;

    public BackgroundMemoryReclaimer(BackgroundMemoryOptions options, ConfigService config)
    {
        _options = options;
        _config = config;
    }

    
    
    
    public static bool ShouldStart(bool aggressiveEnabled, BackgroundMemoryOptions options)
        => aggressiveEnabled && options.FirstTrimDelay > TimeSpan.Zero;

    public void BeginHiddenSession()
    {
        
        if (!ShouldStart(_config.Config.AggressiveBackgroundReclaim, _options)) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void EndHiddenSession() => _cts?.Cancel();

    public void Shutdown()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    
    private async Task RunAsync(CancellationToken token)
    {
        if (_options.FirstTrimDelay <= TimeSpan.Zero) return;
        if (!await DelayAsync(_options.FirstTrimDelay, token).ConfigureAwait(false)) return;
        Reclaim();
    }

    
    private void Reclaim()
    {
        MemoryDiagnostics.CompactManagedHeap();
        MemoryDiagnostics.EmptyProcessWorkingSet();
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
