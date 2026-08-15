using CommunityToolkit.Mvvm.ComponentModel;
using MemeManager.Infrastructure;

namespace MemeManager.Services;

// 更新检查状态（设置页"前往下载新版本"Flyout 显示用）
public enum UpdateCheckState
{
    Idle,      // 尚未检查过（默认；用户需点"检查更新"才会发起请求，符合"不打扰"设计）
    Checking,  // 检查中
    UpToDate,  // 已是最新
    HasUpdate, // 发现新版本
    Failed     // 检查失败（网络/超时/所有源都未返回有效版本号）
}

// 更新检查编排器（单例）：并发请求所有更新源，取第一个成功返回的版本号，
// 立即取消其余源（避免多余网络请求）；结果存属性供 UI 绑定。
// 整个编排内部所有 await 均 ConfigureAwait(false)，从 UI 线程触发也不在
// UI 上下文续跑、不阻塞 UI；需要刷新 UI 的地方（SettingsViewModel）自己 await。
public partial class UpdateService : ObservableObject
{
    private readonly IReadOnlyList<IUpdateServiceClient> _clients;

    public UpdateService(IEnumerable<IUpdateServiceClient> clients)
        => _clients = clients.ToArray();

    // 最近一次检查得到的最新版本号（无结果时为 null）
    [ObservableProperty]
    public partial string? LatestVersion { get; set; }

    [ObservableProperty]
    public partial UpdateCheckState CheckState { get; set; } = UpdateCheckState.Idle;

    // 是否正在检查（防重入 + UI 可显示"检查中"）
    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    // 是否有比当前版本更新的版本（按钮右上角 badge 的依据）。
    // 当前版本是本地 dev build 等无法比较时恒为 false，不提示更新。
    public bool HasNewVersion { get; private set; }

    // 触发一次更新检查。进行中再次调用会被短路忽略（不需要锁）。
    // 失败不重试（按设计）；用户可再次调用。
    public async Task CheckAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        CheckState = UpdateCheckState.Checking;
        Logger.Log($"[UpdateCheck] 开始检查更新");
        try
        {
            using var cts = new CancellationTokenSource();
            // TryGetLatestAsync 兜底：个别源在"获取 Task 时"即同步抛异常
            // （实现不守"失败返回 null"契约）也不能拖垮整体。
            var pending = _clients
                .Select(c => TryGetLatestAsync(c, cts.Token))
                .ToList();

            string? winner = null;
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);

                string? v;
                try
                {
                    v = await done.ConfigureAwait(false);
                }
                catch
                {
                    v = null; // 个别源抛异常不影响整体
                }

                if (!string.IsNullOrWhiteSpace(v))
                {
                    winner = v;
                    cts.Cancel(); // 已拿到结果，取消其余源
                    break;
                }
            }
            var localVersion = Utils.GetInformationalVersion();
            LatestVersion = winner;
            HasNewVersion = VersionString.IsNewer(winner, localVersion);
            OnPropertyChanged(nameof(HasNewVersion));

            if (string.IsNullOrEmpty(winner))
            {
                Logger.Log($"[UpdateCheck] 获取最新Release版本号失败");
                CheckState = UpdateCheckState.Failed;
            }
            else
            {
                Logger.Log($"[UpdateCheck] 成功获取到最新Release版本号: {winner}");
                CheckState = HasNewVersion ? UpdateCheckState.HasUpdate : UpdateCheckState.UpToDate;
            }
        }
        finally
        {
            IsChecking = false;
        }
    }

    // 取 client 的查询任务；client 若在"获取 Task 阶段"即同步抛异常
    // （实现不守"失败返回 null"契约），退化为 null 结果，不影响整体编排。
    private static Task<string?> TryGetLatestAsync(IUpdateServiceClient client, CancellationToken ct)
    {
        try
        {
            return client.GetLatestVersionAsync(ct);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }
}
