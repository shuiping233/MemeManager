using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;

namespace MemeManager.Helpers;

/// <summary>
/// 批量操作的进度状态快照。只承载“事实”，不含任何派生数据
/// （速度 / 百分比 / ETA 等均由 BatchProgressHelper 依据时间轴计算）。
/// </summary>
/// <param name="Completed">已处理到的 item 数（不会超过 Total）。</param>
/// <param name="Total">总 item 数；未知或无需百分比时可传 0。</param>
/// <param name="CurrentItemName">当前正在处理的条目名（如文件名）；无则不传。</param>
public sealed record BatchProgress(
    uint Completed,
    uint Total,
    string? CurrentItemName = null);

/// <summary>
/// 批量操作进度条的“状态解释器”：接收业务层报告的 BatchProgress（事实），
/// 自行维护时间轴，派生出百分比、处理速度（/s）与 ETA，并降频刷新 UI。
/// 是否启用、阈值判断由调用方决定，本类只负责显示。
/// </summary>
internal sealed class BatchProgressHelper
{
    private readonly InfoBar _infoBar;
    private readonly ProgressBar _bar;
    private readonly TextBlock _countText;

    // 测速 / ETA 状态
    private Stopwatch? _sw;
    private uint _lastCompleted;
    private TimeSpan _lastSample;
    private double _speed;          // 平滑后的速度（item/s）
    private int _speedSamples;      // 已采样的次数，用于守卫 ETA 显示
    private DateTime _lastRender;   // 上次真正写 UI 的时间，用于降频

    // 速率指数平滑系数（0~1，越大越跟手）
    private const double SpeedSmoothing = 0.3;
    // UI 刷新最小间隔（避免高频 Report 时过度刷新）
    private static readonly TimeSpan MinRenderInterval = TimeSpan.FromMilliseconds(100);
    // 至少采样多少次稳定后才显示 ETA（避免开头速率失真导致 ETA 乱跳）
    private const int MinSamplesBeforeEta = 3;

    public BatchProgressHelper(InfoBar infoBar, ProgressBar bar, TextBlock countText)
    {
        _infoBar = infoBar;
        _bar = bar;
        _countText = countText;
    }

    /// <summary>打开 InfoBar 并显示标题，重置进度条、计数与测速状态。</summary>
    public void Show(string title)
    {
        _infoBar.Title = title;
        _bar.Value = 0;
        _countText.Text = "";
        ResetTiming();
        _infoBar.IsOpen = true;
    }

    /// <summary>同 Show，但已知总数时先显示“0/Total”初始文本。</summary>
    public void Show(string title, uint total)
    {
        Show(title);
        if (total > 0) _countText.Text = $"0/{total}";
    }

    private void ResetTiming()
    {
        _sw = Stopwatch.StartNew();
        _lastCompleted = 0;
        _lastSample = TimeSpan.Zero;
        _speed = 0;
        _speedSamples = 0;
        _lastRender = DateTime.MinValue;
    }

    /// <summary>关闭 InfoBar。</summary>
    public void Hide() => _infoBar.IsOpen = false;

    /// <summary>
    /// 生成一个 IProgress&lt;BatchProgress&gt;，后台任务每完成若干 item 调用
    /// Report(new BatchProgress(...)) 即可；helper 内部按时间轴派生速度/ETA 并降频刷新。
    /// </summary>
    public IProgress<BatchProgress> CreateProgress() => new Progress<BatchProgress>(OnReport);

    // 收到一次事实快照：更新测速状态，并按降频节奏刷新 UI。
    private void OnReport(BatchProgress p)
    {
        if (_sw == null) return;

        var elapsed = _sw.Elapsed;

        // 首次出现已完成项才启动测速（避免 Completed 仍为 0 时除零/0 速）
        if (_lastCompleted == 0 && p.Completed > 0)
        {
            _lastCompleted = p.Completed;
            _lastSample = elapsed;
        }
        else if (p.Completed > _lastCompleted)
        {
            var deltaCount = (double)(p.Completed - _lastCompleted);
            var deltaTime = (elapsed - _lastSample).TotalSeconds;
            if (deltaTime > 0)
            {
                var instant = deltaCount / deltaTime;
                // 指数平滑：前几次无历史时直接采用瞬时值
                _speed = _speedSamples == 0 ? instant : _speed + SpeedSmoothing * (instant - _speed);
                _speedSamples++;
            }
            _lastCompleted = p.Completed;
            _lastSample = elapsed;
        }

        // 降频：距上次 UI 刷新不足间隔则跳过本次写入（状态已更新，下次再画）
        var now = DateTime.Now;
        if (now - _lastRender < MinRenderInterval && p.Completed < p.Total)
            return;
        _lastRender = now;

        Render(p);
    }

    // 把当前事实 + 派生数据写成 UI
    private void Render(BatchProgress p)
    {
        if (p.Total > 0)
            _bar.Value = p.Completed >= p.Total ? 100 : (int)(p.Completed * 100.0 / p.Total);

        var text = p.Total > 0 ? $"{p.Completed}/{p.Total}" : $"{p.Completed}";

        // 速度（仅启动测速后显示）
        if (_speedSamples > 0 && _speed > 0)
            text += $" · {(int)_speed}/s";

        // ETA（速率足够稳定后才显示）
        if (p.Total > 0 && _speedSamples >= MinSamplesBeforeEta && _speed > 0)
        {
            var remaining = p.Total - p.Completed;
            var etaSec = remaining / _speed;
            text += $" · 约 {FormatEta(etaSec)}";
        }

        if (!string.IsNullOrEmpty(p.CurrentItemName))
            text += $" · {p.CurrentItemName}";

        _countText.Text = text;
    }

    // 把秒数格式化为简洁的剩余时间文本
    private static string FormatEta(double seconds)
    {
        if (seconds < 1) return "<1s";
        if (seconds < 60) return $"{(int)seconds}s";
        if (seconds < 3600) return $"{(int)(seconds / 60)}m";
        return $"{(int)(seconds / 3600)}h";
    }
}
