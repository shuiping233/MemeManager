using System.Diagnostics;
using System.Globalization;

namespace MemeManager.Infrastructure;

/// <summary>
/// 内存诊断快照（纯数据）。四个指标各管一件事：
///   Managed    —— 托管包装对象有没有放掉（量级小，不是主指标）
///   Private    —— 进程私有提交量，含 native 对象树（判断"控件到底死没死"）
///   WorkingSet —— 任务管理器那一列"内存"（可被 EmptyWorkingSet 压低，但会被自然回填）
///   BitmapImage 计数 —— 图像解码资源有没有断干净
///
/// 不引用 UI / ViewModel（LiveBitmapImages、PageAlive 由调用方传入），因此可脱离 WinUI 单测。
/// 采集本身会分配少量内存，只在隐藏/呼出等低频时点调用。
/// </summary>
public readonly record struct MemorySnapshot(
    long ManagedHeapBytes,
    long PrivateBytes,
    long WorkingSetBytes,
    int LiveBitmapImages,
    bool? PageAlive,
    long TotalAllocatedBytes = -1,
    int Gen0Collections = -1,
    int Gen1Collections = -1,
    int Gen2Collections = -1)
{
    /// <summary>采集当前进程快照（依赖 Process/GC，不做单测）。</summary>
    /// <param name="liveBitmapImages">VM 仍持有的 BitmapImage 数量；负数表示不采集。</param>
    /// <param name="pageAlive">页面弱引用探针结果；null 表示本次未探测。</param>
    public static MemorySnapshot Capture(int liveBitmapImages = -1, bool? pageAlive = null)
    {
        using var process = Process.GetCurrentProcess();
        // Process 的计数器是"最近一次刷新"的快照值，显式 Refresh 以免采样时机误差。
        process.Refresh();
        return new MemorySnapshot(
            GC.GetTotalMemory(false),
            process.PrivateMemorySize64,
            process.WorkingSet64,
            liveBitmapImages,
            pageAlive,
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    /// <summary>
    /// 拼成一行日志（纯函数，可单测）。例：
    /// <c>[Memory] Idle#1 after: Managed=3.4MB Private=137.8MB WorkingSet=2.6MB LiveBitmapImages=0 AllocTotal=14.8MB GC=10/10/10</c>
    ///
    /// 判读要点：`AllocTotal` 是**累计分配量**（只增不减，涨 ≠ 泄漏）；`GC` 是 0/1/2 代回收次数。
    /// 两者配合才能区分"真泄漏"与"垃圾还没被 GC 回收"。
    /// </summary>
    public string ToLogLine(string tag)
    {
        var parts = new List<string>(8)
        {
            $"Managed={MemoryDiagnostics.FormatMB(ManagedHeapBytes)}",
            $"Private={MemoryDiagnostics.FormatMB(PrivateBytes)}",
            $"WorkingSet={MemoryDiagnostics.FormatMB(WorkingSetBytes)}",
        };

        // 未采集的项省略，避免日志里出现误导性的 -1。
        if (LiveBitmapImages >= 0)
            parts.Add($"LiveBitmapImages={LiveBitmapImages}");
        if (PageAlive.HasValue)
            parts.Add($"PageAlive={PageAlive.Value}");
        if (TotalAllocatedBytes >= 0)
            parts.Add($"AllocTotal={MemoryDiagnostics.FormatMB(TotalAllocatedBytes)}");
        if (Gen0Collections >= 0)
            parts.Add($"GC={Gen0Collections}/{Gen1Collections}/{Gen2Collections}");

        return $"[Memory] {tag}: {string.Join(' ', parts)}";
    }
}

/// <summary>
/// 内存诊断与后台回收动作的唯一入口（开关 / 采集 / 格式化 / GC / 工作集裁剪），便于后续调试直接调用。
/// 诊断开关见 <see cref="AppConstants.EnableMemoryDiagnostics"/>；后台回收的节奏见
/// <see cref="AppConstants.WorkingSetTrimDelay"/> 与 <see cref="AppConstants.IdleMemoryProbeInterval"/>。
/// </summary>
public static class MemoryDiagnostics
{
    /// <summary>诊断总开关：关闭时零输出、零采集（回收动作不受它影响）。</summary>
    public static bool Enabled => AppConstants.EnableMemoryDiagnostics;

    public static MemorySnapshot Capture(int liveBitmapImages = -1, bool? pageAlive = null)
        => MemorySnapshot.Capture(liveBitmapImages, pageAlive);

    /// <summary>按开关输出一行快照。只在隐藏/呼出等低频时点调用。</summary>
    public static void Log(string tag, int liveBitmapImages = -1, bool? pageAlive = null)
    {
        if (!Enabled) return;
        Logger.Log(Capture(liveBitmapImages, pageAlive).ToLogLine(tag));
    }

    /// <summary>字节 → MB 文本（1 位小数；负数用于差值）。纯函数。</summary>
    public static string FormatMB(long bytes)
        => ((bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture)) + "MB";

    /// <summary>差值文本：<c>-90.5MB (-33.4%)</c>；before 为 0 时百分比退化为 <c>n/a</c>。纯函数。</summary>
    public static string FormatDelta(long before, long after)
    {
        long delta = after - before;
        string percent = before == 0
            ? "n/a"
            : ((delta * 100.0 / before).ToString("F1", CultureInfo.InvariantCulture)) + "%";
        return $"{FormatMB(delta)} ({percent})";
    }

    /// <summary>是否该做工作集裁剪（纯函数）：延迟已配置、窗口不可见、且不在退出流程中。</summary>
    public static bool ShouldTrimWorkingSet(TimeSpan delay, bool isVisible, bool isClosing)
        => delay > TimeSpan.Zero && ShouldRunHiddenMaintenance(isVisible, isClosing);

    /// <summary>隐藏态维护动作（延迟裁剪 / 周期维护）是否该继续：不可见且不在退出流程中。纯函数。</summary>
    public static bool ShouldRunHiddenMaintenance(bool isVisible, bool isClosing)
        => !isVisible && !isClosing;

    /// <summary>
    /// 强制压缩式 GC（含 LOH 压缩）+ 跑完终结器队列。
    /// 必须等"元素已从视觉树摘除"若干秒后再跑才有效——隐藏瞬间跑几乎回收不到东西（WinRT 引用尚未断）。
    /// </summary>
    public static void CompactManagedHeap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>
    /// 裁剪进程工作集（EmptyWorkingSet）。返回是否成功。
    /// 只压 Working Set，对 Private Bytes 零贡献（实测）；效果是暂时的，页会被后续访问自然回填。
    /// </summary>
    public static bool EmptyProcessWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return NativeMethods.EmptyWorkingSet(process.Handle);
    }
}
