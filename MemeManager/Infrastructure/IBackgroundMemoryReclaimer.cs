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
    /// <param name="liveBitmapImageCount">
    /// 采样时读取"仍存活的 BitmapImage 数量"的委托。由 View 层传入，避免本层依赖 ViewModel。
    /// </param>
    /// <param name="pageProbe">
    /// 可选的页面弱引用探针：用于验证"隐藏后页面是否真的可被回收"（诊断用，可为 null）。
    /// </param>
    void BeginHiddenSession(Func<int>? liveBitmapImageCount = null, WeakReference? pageProbe = null);

    /// <summary>退出隐藏态（窗口重新可见）：取消所有待执行与进行中的回收动作。</summary>
    void EndHiddenSession();

    /// <summary>进程退出：取消并释放，避免退出期间还挂着任务。</summary>
    void Shutdown();
}
