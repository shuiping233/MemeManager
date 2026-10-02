namespace MemeManager.Infrastructure;

/// <summary>
/// 后台内存回收策略：主窗口隐藏后延迟做一次「强制 GC + 工作集裁剪」，窗口可见或进程退出时取消。
///
/// 目的：让隐藏后的驻留内存（Working Set）尽快降下来，而不必等进程退出。
/// 具体动作与理由见 <see cref="BackgroundMemoryReclaimer"/>。
/// </summary>
public interface IBackgroundMemoryReclaimer
{
    /// <summary>
    /// 进入隐藏态：安排一次延迟回收。重复调用会重置为一次新会话（计时重新开始）。
    /// </summary>
    void BeginHiddenSession();

    /// <summary>退出隐藏态（窗口重新可见）：取消所有待执行与进行中的回收动作。</summary>
    void EndHiddenSession();

    /// <summary>进程退出：取消并释放，避免退出期间还挂着任务。</summary>
    void Shutdown();
}
