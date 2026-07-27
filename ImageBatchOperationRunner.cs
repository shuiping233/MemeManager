using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MemeManager.Models;
using Microsoft.UI.Dispatching;

namespace MemeManager.Helpers;

/// <summary>
/// 批量操作类型。决定进度条阈值、是否为“写操作”（需占用写入锁）以及
/// 完成后默认的 UI 刷新策略。
/// </summary>
public enum BatchOperationKind
{
    Import,
    Export,
    Move,
    Delete
}

/// <summary>
/// 批量操作所需的 UI 侧回调集合。由于进度完成后需要在 UI 线程统一刷新图片控件，
/// 而 runner 本身不持有 MainWindow 的私有方法，故由 MainWindow 在构造时注入这些回调。
/// 所有回调都保证在 UI 线程执行。
/// </summary>
public sealed class BatchUiContext
{
    public required Func<bool> IsClosing { get; init; }
    public required Func<bool> IsVisible { get; init; }
    public required Func<string> CurrentCategory { get; init; }
    public required Func<bool> IsAllMemesView { get; init; }
    public required Action UpdateCategoryCounts { get; init; }
    public required Action RefreshMemes { get; init; }
    public required Action<IEnumerable<MemeModel>> RemoveFromCurrentView { get; init; }
}

/// <summary>
/// 图片批量操作的统一编排器：
/// 1. 把“后台线程执行 + 进度条显示 + UI 线程收尾刷新 + 关闭/隐藏守卫”整条链路收口，
///    入口（按钮 / 右键 / 拖拽 / 文件监听）只负责收集参数并调用 RunAsync，不再各自重写一遍。
/// 2. 按 kind 自动选择进度条阈值与默认标题，并内置“写完才刷 UI”的分类守卫逻辑。
/// 3. 用 IsWriteActive 标志挡住“用户主动发起的写操作”并发（导入/移动/删除），
///    导出（copy 语义）与文件监听触发的导入不在其列——后者由用户自行 F5 刷新，不兜底。
/// 4. 完成后默认 UI 刷新策略（按 kind 自动）：
///    - Import：始终更新分类计数；仅当用户仍停留在导入时的分类才 RefreshMemes。
///    - Export：不改缓存，无需刷新。
///    - Move / Delete：从当前视图移除受影响项 + 更新分类计数。
/// </summary>
internal sealed class ImageBatchOperationRunner
{
    // 各操作的进度条显示阈值（低于该数量不弹 InfoBar）
    private const int ImportThreshold = 5;
    private const int ExportThreshold = 5;
    private const int DeleteThreshold = 10;
    private const int MoveThreshold = 100;

    private static int ThresholdFor(BatchOperationKind kind) => kind switch
    {
        BatchOperationKind.Import => ImportThreshold,
        BatchOperationKind.Export => ExportThreshold,
        BatchOperationKind.Delete => DeleteThreshold,
        BatchOperationKind.Move => MoveThreshold,
        _ => 0
    };

    // 写操作（会改缓存 + 磁盘），需要占用写入锁避免并发写
    private static bool IsWriteKind(BatchOperationKind kind) =>
        kind is BatchOperationKind.Import or BatchOperationKind.Move or BatchOperationKind.Delete;

    private static string DefaultTitle(BatchOperationKind kind) => kind switch
    {
        BatchOperationKind.Import => Localization.Get("Batch_Importing"),
        BatchOperationKind.Export => Localization.Get("Batch_Exporting"),
        BatchOperationKind.Move => Localization.Get("Batch_Moving"),
        BatchOperationKind.Delete => Localization.Get("Batch_Deleting"),
        _ => ""
    };

    private readonly BatchProgressHelper _progress;
    private readonly DispatcherQueue _dispatcher;
    private readonly BatchUiContext _ui;

    // 写操作占用标志：true 表示当前有用户主动发起的写任务在跑。
    // 用 Interlocked 保证入口判断与设置之间的线程安全。
    private int _writeActive;

    public bool IsWriteActive => _writeActive != 0;

    public ImageBatchOperationRunner(BatchProgressHelper progress, DispatcherQueue dispatcher, BatchUiContext ui)
    {
        _progress = progress;
        _dispatcher = dispatcher;
        _ui = ui;
    }

    /// <summary>
    /// 统一执行一次批量操作。
    /// </summary>
    /// <param name="kind">操作类型（决定阈值/标题/写锁/刷新策略）。</param>
    /// <param name="totalCount">总 item 数，用于阈值判断与初始“0/Total”显示。</param>
    /// <param name="work">后台线程执行的实际工作，接收 IProgress&lt;BatchProgress&gt; 用于回报进度。</param>
    /// <param name="affectedModels">受影响（将被移除出当前视图）的模型，Move/Delete 用。</param>
    /// <param name="targetCategory">导入目标分类；用于“仍停留在该分类才刷新”的分类守卫。</param>
    /// <param name="onUiComplete">在 UI 线程、守卫通过后、自动刷新之后额外执行的回调（如单张重复弹窗）。</param>
    /// <param name="titleOverride">覆盖默认标题。</param>
    /// <param name="occupyWriteLock">
    /// 是否占用写入锁（默认 true）。写操作（导入/移动/删除）应占用，以防止用户并发写入；
    /// 文件监听等“外部触发、不归用户主动发起”的导入传 false，避免误挡用户操作（用户自行改
    /// 目录应自己 F5 刷新，不在此兜底）。
    /// </param>
    public async Task RunAsync(
        BatchOperationKind kind,
        int totalCount,
        Func<IProgress<BatchProgress>, Task> work,
        IEnumerable<MemeModel>? affectedModels = null,
        string? targetCategory = null,
        Action? onUiComplete = null,
        string? titleOverride = null,
        bool occupyWriteLock = true)
    {
        bool wasWrite = false;
        try
        {
            // 写操作：占用写入锁（入口已先行判断，这里仅作兜底设置）
            if (IsWriteKind(kind) && occupyWriteLock)
            {
                wasWrite = true;
                Interlocked.Exchange(ref _writeActive, 1);
            }

            bool showProgress = totalCount >= ThresholdFor(kind);
            if (showProgress)
                _progress.Show(titleOverride ?? DefaultTitle(kind), (uint)totalCount);

            IProgress<BatchProgress> progress = showProgress
                ? _progress.CreateProgress()
                : new Progress<BatchProgress>(_ => { });

            // 整个工作搬到线程池，避免 UI 线程被逐张 IO/缓存写入占满
            await Task.Run(() => work(progress));

            // 写锁在工作完成后立即释放（不等待 UI 收尾），避免收尾刷新（如重建网格）
            // 在 UI 线程执行期间仍占用写锁，导致后续用户操作（如再次拖入）的 guard 被延迟放行/拒绝。
            if (wasWrite)
                Interlocked.Exchange(ref _writeActive, 0);

            // 回到 UI 线程收尾（守卫 + 自动刷新 + 隐藏）。
            // 用 Low 优先级入队：收尾刷新（尤其导入后重建网格）是重活，降到输入事件/渲染之后执行，
            // 避免与用户紧接着的操作（如再次拖入文件）在 UI 线程同一帧抢执行权造成卡顿。
            var tcs = new TaskCompletionSource();
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    if (_ui.IsClosing() || !_ui.IsVisible())
                    {
                        if (showProgress) _progress.Hide();
                        return;
                    }

                    ApplyAutoRefresh(kind, affectedModels, targetCategory);

                    onUiComplete?.Invoke();

                    if (showProgress) _progress.Hide();
                }
                finally
                {
                    tcs.TrySetResult();
                }
            });
            await tcs.Task;
        }
        finally
        {
            // 兜底：若上面未因正常路径释放（如 work 抛异常跳过释放点），确保写锁一定归零。
            if (wasWrite)
                Interlocked.Exchange(ref _writeActive, 0);
        }
    }

    // 按 kind 自动刷新 UI 控件（UI 线程）
    private void ApplyAutoRefresh(BatchOperationKind kind, IEnumerable<MemeModel>? affectedModels, string? targetCategory)
    {
        switch (kind)
        {
            case BatchOperationKind.Import:
                _ui.UpdateCategoryCounts();
                // 刷新右侧图片容器的条件：
                //  - 用户仍停留在导入时的分类；或
                //  - 当前是“全部表情”聚合视图（导入到任何分类都应即时出现在全部表情里）。
                if (targetCategory != null &&
                    (_ui.IsAllMemesView() ||
                     _ui.CurrentCategory().Equals(targetCategory, StringComparison.OrdinalIgnoreCase)))
                    _ui.RefreshMemes();
                break;

            case BatchOperationKind.Export:
                // 不改缓存，无需刷新
                break;

            case BatchOperationKind.Move:
            case BatchOperationKind.Delete:
                if (affectedModels != null)
                    _ui.RemoveFromCurrentView(affectedModels);
                _ui.UpdateCategoryCounts();
                break;
        }
    }
}
