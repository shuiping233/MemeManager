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
    
    
    
    public static MemorySnapshot Capture(int liveBitmapImages = -1, bool? pageAlive = null)
    {
        using var process = Process.GetCurrentProcess();
        
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

    
    
    
    
    
    
    
    public string ToLogLine(string tag)
    {
        var parts = new List<string>(8)
        {
            $"Managed={MemoryDiagnostics.FormatMB(ManagedHeapBytes)}",
            $"Private={MemoryDiagnostics.FormatMB(PrivateBytes)}",
            $"WorkingSet={MemoryDiagnostics.FormatMB(WorkingSetBytes)}",
        };

        
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






public static class MemoryDiagnostics
{
    
    public static bool Enabled => AppConstants.EnableMemoryDiagnostics;

    public static MemorySnapshot Capture(int liveBitmapImages = -1, bool? pageAlive = null)
        => MemorySnapshot.Capture(liveBitmapImages, pageAlive);

    
    public static void Log(string tag, int liveBitmapImages = -1, bool? pageAlive = null)
    {
        if (!Enabled) return;
        Logger.Log(Capture(liveBitmapImages, pageAlive).ToLogLine(tag));
    }

    
    public static string FormatMB(long bytes)
        => ((bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture)) + "MB";

    
    public static string FormatDelta(long before, long after)
    {
        long delta = after - before;
        string percent = before == 0
            ? "n/a"
            : ((delta * 100.0 / before).ToString("F1", CultureInfo.InvariantCulture)) + "%";
        return $"{FormatMB(delta)} ({percent})";
    }

    
    
    
    
    public static void CompactManagedHeap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    
    
    
    
    public static bool EmptyProcessWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return NativeMethods.EmptyWorkingSet(process.Handle);
    }
}
