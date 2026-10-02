using MemeManager.Services;

namespace MemeManager.Infrastructure;

/// <summary>
/// 后台内存回收策略的参数。默认取 <see cref="AppConstants"/>，也可单独注入以便测试或调参。
/// </summary>
/// <param name="FirstTrimDelay">窗口隐藏后，隔多久做第一次回收（延迟是为了避开"隐藏后马上呼出"）。</param>
/// <param name="MaintenanceInterval">首次回收之后，每隔多久维护一次（&lt;= 0 表示不周期维护）。</param>
/// <param name="MaintenanceRounds">周期维护的轮数上限；<b>0 或负数表示无限轮</b>（直到窗口重新可见）。</param>
public sealed record BackgroundMemoryOptions(
    TimeSpan FirstTrimDelay,
    TimeSpan MaintenanceInterval,
    int MaintenanceRounds)
{
    public static BackgroundMemoryOptions FromAppConstants() => new(
        AppConstants.WorkingSetTrimDelay,
        AppConstants.IdleMemoryProbeInterval,
        AppConstants.IdleMemoryProbeTimes);
}

/// <summary>
/// <see cref="IBackgroundMemoryReclaimer"/> 的默认实现。时间顺序：
///
/// ```text
/// 窗口隐藏
///   │  等 FirstTrimDelay（默认 5s，避开"隐藏后马上呼出"）
///   ├─ 强制 GC（含终结器队列）→ EmptyWorkingSet        ← 首次回收
///   │     · GC 才是拿回 Private Bytes 的那一步（实测 -47MB：元素摘除后 WinRT 引用是异步断开的，
///   │       隐藏瞬间 GC 几乎回收不到东西，必须等几秒）
///   │     · EmptyWorkingSet 只压 Working Set（实测对 Private 零贡献），且效果是暂时的
///   │  之后每 MaintenanceInterval（默认 30s）重复一次同样动作，直到窗口可见或达到轮数上限
/// 窗口可见 / 进程退出 → 立即取消
/// ```
///
/// 线程：全部在后台线程执行（GC 与工作集裁剪不需要 UI 线程），因此不会阻塞 UI，
/// 也天然串行（单条 async 流程 + CancellationToken），无需加锁。
/// </summary>
public sealed class BackgroundMemoryReclaimer : IBackgroundMemoryReclaimer
{
    private readonly BackgroundMemoryOptions _options;
    private readonly ConfigService _config;

    private CancellationTokenSource? _cts;
    private Func<int>? _liveBitmapImageCount;
    private WeakReference? _pageProbe;
    private MemorySnapshot? _lastSnapshot;

    public BackgroundMemoryReclaimer(BackgroundMemoryOptions options, ConfigService config)
    {
        _options = options;
        _config = config;
    }

    /// <summary>
    /// 是否应当启动隐藏态回收（纯函数，可单测）：总开关开启，且至少配置了一种动作
    /// （首次回收的延迟 &gt; 0，或周期维护间隔 &gt; 0）。两者都为 0/负 = 该策略整体禁用。
    /// </summary>
    public static bool ShouldStart(bool aggressiveEnabled, BackgroundMemoryOptions options)
        => aggressiveEnabled
           && (options.FirstTrimDelay > TimeSpan.Zero || options.MaintenanceInterval > TimeSpan.Zero);

    public void BeginHiddenSession(Func<int>? liveBitmapImageCount = null, WeakReference? pageProbe = null)
    {
        // 总开关关闭：完全不介入，隐藏流程保持"只做 x:Load 卸载 + 一次 GC"的原有行为。
        if (!ShouldStart(_config.Config.AggressiveBackgroundReclaim, _options)) return;

        _liveBitmapImageCount = liveBitmapImageCount;
        _pageProbe = pageProbe;
        _lastSnapshot = null;

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
        if (_options.FirstTrimDelay > TimeSpan.Zero)
        {
            if (!await DelayAsync(_options.FirstTrimDelay, token).ConfigureAwait(false)) return;
            FirstReclaim();
        }

        if (_options.MaintenanceInterval <= TimeSpan.Zero) return;

        for (int round = 1; !token.IsCancellationRequested; round++)
        {
            if (!await DelayAsync(_options.MaintenanceInterval, token).ConfigureAwait(false)) return;
            Maintain(round);
            if (_options.MaintenanceRounds > 0 && round >= _options.MaintenanceRounds) return;
        }
    }

    // 首次回收：一次强制 GC + 工作集裁剪，并记录三步快照（Private 的变化基本都发生在 GC 那一步）。
    private void FirstReclaim()
    {
        MemoryDiagnostics.Log("BeforeReclaim", LiveBitmapImages());
        MemoryDiagnostics.CompactManagedHeap();
        MemoryDiagnostics.Log("AfterCompactGc", LiveBitmapImages());
        MemoryDiagnostics.EmptyProcessWorkingSet();
        MemoryDiagnostics.Log("AfterReclaim", LiveBitmapImages(), ProbePageAlive());
    }

    // 周期维护：GC + 裁剪，并打出相对上一轮的增量。
    // 判读：`after` 的 ΔManaged 若每轮都把那点增长收回去（负数），说明只是"垃圾未被回收"而非泄漏；
    // 若某轮起收不回去，才是真泄漏（那时该用 dotnet-trace 找引用根）。
    private void Maintain(int round)
    {
        var before = MemoryDiagnostics.Capture(LiveBitmapImages());
        if (MemoryDiagnostics.Enabled)
        {
            string allocSinceLast = _lastSnapshot is { } prev
                ? $" ΔAlloc={MemoryDiagnostics.FormatMB(before.TotalAllocatedBytes - prev.TotalAllocatedBytes)}"
                : string.Empty;
            Logger.Log(before.ToLogLine($"Idle#{round} before") + allocSinceLast);
        }

        MemoryDiagnostics.CompactManagedHeap();
        MemoryDiagnostics.EmptyProcessWorkingSet();

        var after = MemoryDiagnostics.Capture(LiveBitmapImages());
        _lastSnapshot = after;
        if (MemoryDiagnostics.Enabled)
        {
            Logger.Log(after.ToLogLine($"Idle#{round} after") +
                       $" ΔManaged={MemoryDiagnostics.FormatDelta(before.ManagedHeapBytes, after.ManagedHeapBytes)}" +
                       $" ΔPrivate={MemoryDiagnostics.FormatDelta(before.PrivateBytes, after.PrivateBytes)}" +
                       $" ΔWS={MemoryDiagnostics.FormatDelta(before.WorkingSetBytes, after.WorkingSetBytes)}");
        }
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

    private int LiveBitmapImages() => _liveBitmapImageCount?.Invoke() ?? -1;

    // 页面弱引用探针：GC 后页面是否仍存活。页面被回收后清空探针，便于下次会话重新采集。
    private bool? ProbePageAlive()
    {
        if (_pageProbe is null) return null;
        bool alive = _pageProbe.IsAlive;
        if (!alive) _pageProbe = null;
        return alive;
    }
}
