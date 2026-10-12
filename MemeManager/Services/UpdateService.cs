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

    
    [ObservableProperty]
    public partial string? LatestVersion { get; set; }

    [ObservableProperty]
    public partial UpdateCheckState CheckState { get; set; } = UpdateCheckState.Idle;

    
    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    
    
    public bool HasNewVersion { get; private set; }

    // 触发一次更新检查。进行中再次调用会被短路忽略（不需要锁）。
    public async Task CheckAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        CheckState = UpdateCheckState.Checking;
        Logger.Log($"[UpdateCheck] 开始检查更新");
        try
        {
            using var cts = new CancellationTokenSource();
            
            
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
                    v = null; 
                }

                if (!string.IsNullOrWhiteSpace(v))
                {
                    winner = v;
                    cts.Cancel(); 
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
