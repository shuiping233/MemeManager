using System.Text.Json;
using MemeManager.Infrastructure;

namespace MemeManager.Services;

// GitHub 更新源：GET https://api.github.com/repos/{owner}/{repo}/releases/latest
// 返回 JSON，最新版本号在根对象的 tag_name 字段（如 "v1.12.12"）。
public sealed class GithubReleaseClient : UpdateServiceClientBase
{
    public override string SourceName => "GitHub";

    private const string ApiUrl =
        "https://api.github.com/repos/shuiping233/MemeManager/releases/latest";

    public override async Task<string?> GetLatestVersionAsync(CancellationToken ct = default)
    {
        var text = await FetchAsync(SourceName, ApiUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tag))
                return null;
            var tagName = tag.GetString();
            // 只认合法版本号，远端数据异常（乱串）时不当作版本
            return VersionString.TryParse(tagName, out _) ? tagName : null;
        }
        catch (Exception ex)
        {
            Logger.Log($"[UpdateCheck] GitHub 响应解析失败: {ex.Message}");
            return null;
        }
    }
}
