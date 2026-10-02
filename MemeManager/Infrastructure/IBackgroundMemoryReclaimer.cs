namespace MemeManager.Infrastructure;

/// <summary>
/// 后台内存回收策略：主窗口隐藏期间做「延迟首次回收 + 周期维护」，窗口可见或进程退出时全部取消。
///
/// 目的：让隐藏后的驻留内存（Working Set / Managed Heap）尽快降下来并保持低位，
/// 而不是靠"关闭时一次性释放"后又被后台活动慢慢涨回去。
/// 具体动作与理由见 <see cref="BackgroundMemoryReclaimer"/>。
/// </summary>
public interface IBackgroundMemoryReclaimer
{
    /// <summary>
    /// 进入隐藏态：安排延迟首次回收，随后按间隔周期维护。重复调用会重置为一次新会话。
    /// </summary>
    void BeginHiddenSession();

    /// <summary>退出隐藏态（窗口重新可见）：取消所有待执行与进行中的回收动作。</summary>
    void EndHiddenSession();

    /// <summary>进程退出：取消并释放，避免退出期间还挂着任务。</summary>
    void Shutdown();
}
