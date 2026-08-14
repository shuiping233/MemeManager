using MemeManager.Infrastructure;

namespace MemeManager.Services;

// 更新源客户端共享基类：持有进程级 HttpClient 单例（所有更新源共用，均只发简单 GET）。
// 单例避免每次请求重建连接（SSL 握手开销大）；10s 超时——更新检查是后台任务，失败静默即可。
// 统一关闭自动跟随重定向：更新源端点可能返回 307（如 CNB），由子类自行决定如何解析响应。
public abstract class UpdateServiceClientBase : IUpdateServiceClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public abstract string SourceName { get; }

    public abstract Task<string?> GetLatestVersionAsync(CancellationToken ct = default);

    // 发起 GET 并返回"解析来源文本"：优先取重定向 Location 头（307 场景），
    // 否则取响应体（2xx）。任何异常/取消/非 2xx 且无 Location 均返回 null。
    protected static async Task<string?> FetchAsync(string sourceName, string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // GitHub API 强制要求 User-Agent，否则 403；对其他源无害，统一携带。
            req.Headers.TryAddWithoutValidation("User-Agent", "MemeManager");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            var location = resp.Headers.Location?.ToString();
            if (!string.IsNullOrWhiteSpace(location))
                return location;

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log($"[UpdateCheck] {sourceName} 响应状态异常: {(int)resp.StatusCode}");
                return null;
            }

            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null; // 超时或被 UpdateService 取消，均视为无结果
        }
        catch (Exception ex)
        {
            Logger.Log($"[UpdateCheck] {sourceName} 请求失败: {ex.Message}");
            return null;
        }
    }
}
