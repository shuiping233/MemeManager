using System.Diagnostics;
using System.Globalization;

namespace MemeManager.Infrastructure;

/// <summary>
/// 内存诊断快照（只读纯数据）。
///
/// 为什么需要它：WinUI 3 的托管堆只有几 MB，控件真正的占用在 native 侧（XAML 对象树 /
/// Composition / DWrite 字体缓存），托管对象只是"总闸"——托管包装还活着，native 对象就不会析构。
/// 因此判断"隐藏后到底有没有回收"必须同时看四项：
///
///   Managed Heap   —— 托管包装对象是否放掉（量级小，不是主指标）
///   Private Bytes  —— 进程私有提交量，含 native 对象树（判断"控件到底死没死"）
///   Working Set    —— 任务管理器里那一列"内存"（可被 EmptyWorkingSet 主动压低）
///   BitmapImage 数 —— 图像解码资源是否断干净（见 MemeViewModel.LiveBitmapImageCount）
///
/// 分层纪律：本类型不引用 UI / ViewModel——LiveBitmapImages 与 PageAlive 一律由调用方传入，
/// 因此可脱离 WinUI 单测（见 MemeManager.Tests/MemoryDiagnosticsTests.cs）。
/// 诊断总开关见 <see cref="AppConstants.EnableMemoryDiagnostics"/>。
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
    /// 格式化为一行日志（纯函数，可单测）。
    /// 例：<c>[Memory] IdleProbe#1: Managed=4.7MB Private=157.7MB WorkingSet=35.6MB LiveBitmapImages=0 AllocTotal=812.3MB GC=3/1/1</c>
    ///
    /// `AllocTotal` = 进程生命周期累计分配量，`GC` = 0/1/2 代回收次数。
    /// **两者配合才能区分"真泄漏"与"垃圾还没被回收"**：分配一路涨而 GC 次数不动 = 后者
    /// （分配太慢，gen0 预算没填满，GC 自然不触发），见 todo.md §0.6。
    /// </summary>
    public string ToLogLine(string tag)
    {
        var parts = new List<string>(8)
        {
            $"Managed={MemoryDiagnostics.FormatMB(ManagedHeapBytes)}",
            $"Private={MemoryDiagnostics.FormatMB(PrivateBytes)}",
            $"WorkingSet={MemoryDiagnostics.FormatMB(WorkingSetBytes)}",
        };

        // 未采集的项直接省略，避免日志里出现误导性的 -1 / 空值。
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

/// <summary>内存诊断入口：开关 + 采集 + 纯格式化函数。</summary>
public static class MemoryDiagnostics
{
    /// <summary>诊断总开关（<see cref="AppConstants.EnableMemoryDiagnostics"/>）。关闭时零输出、零采集。</summary>
    public static bool Enabled => AppConstants.EnableMemoryDiagnostics;

    public static MemorySnapshot Capture(int liveBitmapImages = -1, bool? pageAlive = null)
        => MemorySnapshot.Capture(liveBitmapImages, pageAlive);

    /// <summary>按开关输出一行内存快照。只在隐藏/呼出等低频时点调用。</summary>
    public static void Log(string tag, int liveBitmapImages = -1, bool? pageAlive = null)
    {
        if (!Enabled) return;
        Logger.Log(Capture(liveBitmapImages, pageAlive).ToLogLine(tag));
    }

    /// <summary>字节 → MB 文本（固定 1 位小数；负数用于差值展示）。纯函数。</summary>
    public static string FormatMB(long bytes)
        => ((bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture)) + "MB";

    /// <summary>
    /// 差值文本：<c>-90.5MB (-33.4%)</c>。before 为 0 时百分比退化为 <c>n/a</c>（避免除零）。纯函数。
    /// </summary>
    public static string FormatDelta(long before, long after)
    {
        long delta = after - before;
        string percent = before == 0
            ? "n/a"
            : ((delta * 100.0 / before).ToString("F1", CultureInfo.InvariantCulture)) + "%";
        return $"{FormatMB(delta)} ({percent})";
    }

    /// <summary>
    /// 是否应当执行工作集裁剪（纯函数，可单测）：延迟已配置、窗口仍不可见、且不在退出流程中。
    /// 三个条件缺一不可——延迟为 0/负表示禁用；可见时裁剪会让用户立刻感到缺页卡顿。
    /// </summary>
    public static bool ShouldTrimWorkingSet(TimeSpan delay, bool isVisible, bool isClosing)
        => delay > TimeSpan.Zero && ShouldRunHiddenMaintenance(isVisible, isClosing);

    /// <summary>
    /// 隐藏态维护动作（延迟 trim / 稳态观察探针）是否应当继续：窗口不可见且不在退出流程中。纯函数。
    /// 判据来自实测：可见时执行这些动作只会让用户感到缺页卡顿，退出时执行毫无意义。
    /// </summary>
    public static bool ShouldRunHiddenMaintenance(bool isVisible, bool isClosing)
        => !isVisible && !isClosing;

    /// <summary>
    /// 强制压缩式 GC（含 LOH 压缩）+ 跑完终结器队列。
    /// 与 <see cref="EmptyProcessWorkingSet"/> 刻意拆开，是为了能分别打点、定位"到底哪一步回收了内存"。
    /// </summary>
    public static void CompactManagedHeap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>
    /// 裁剪进程工作集（EmptyWorkingSet）。返回是否成功。
    /// 实测结论（todo.md §0.6）：它**只压 Working Set**（321MB → 4.7MB），对 Private Bytes 贡献为 0 ——
    /// 上一版日志里"Private 降 ~60MB"其实全部来自 <see cref="CompactManagedHeap"/>（当时两者合并打点，现已拆开）。
    /// </summary>
    public static bool EmptyProcessWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return NativeMethods.EmptyWorkingSet(process.Handle);
    }
}
