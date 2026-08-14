using MemeManager.Infrastructure;

namespace MemeManager.Services;

// CNB 更新源：GET https://cnb.cool/{owner}/{repo}/-/releases/latest
// 该端点返回 307 重定向，Location 头与响应体都是同样的路径文本
// "/shuiping233/MemeManager/-/releases/tag/v1.12.12"（实测两者同时存在，
// 基类优先取 Location，取不到再读 body）。版本号即路径最后一段。
public sealed class CnbReleaseClient : UpdateServiceClientBase
{
    public override string SourceName => "CNB";

    private const string LatestUrl =
        "https://cnb.cool/shuiping233/MemeManager/-/releases/latest";

    public override async Task<string?> GetLatestVersionAsync(CancellationToken ct = default)
    {
        var path = await FetchAsync(SourceName, LatestUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path)) return null;
        Logger.Log($"[UpdateCheck] 从 CNB Release 请求到了最新版本号");

        var tag = path.Trim().Split('/').LastOrDefault();
        // 只认合法版本号，远端数据异常（乱串）时不当作版本
        return VersionString.TryParse(tag, out _) ? tag : null;
    }
}
