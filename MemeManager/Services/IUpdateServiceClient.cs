namespace MemeManager.Services;

// 更新源客户端：负责向特定 Release 平台请求最新版本号。
// 实现类见 GithubReleaseClient / CnbReleaseClient；新增源只需再实现一个并注册进 DI。
public interface IUpdateServiceClient
{
    // 源名称（日志用），如 "GitHub" / "CNB"
    string SourceName { get; }

    // 请求该平台的最新版本号（如 "v1.12.12"）。
    // 约定：任何失败（网络/超时/取消/响应解析失败）都返回 null，不抛异常，
    // 由 UpdateService 统一编排（先成功者胜，成功后取消其余源）。
    Task<string?> GetLatestVersionAsync(CancellationToken ct = default);
}
