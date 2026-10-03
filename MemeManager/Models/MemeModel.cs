namespace MemeManager.Models;

public class MemeModel
{
    public string Hash { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public string FileName => $"{Hash}{Extension}";

    public string LocalPath { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();

    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    public int UsageCount { get; set; }

    public uint Priority { get; set; }


    /// <summary>
    /// 关键词模糊匹配 Title 或 Tags 时返回 true；keyword 为 null/空/空白时返回 false（若作为搜索过滤用途需要上游自行 if (!string.IsNullOrWhiteSpace(...)) 阻挡无效值进入）。
    /// </summary>
    public bool HasKeyword(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return false;
        if (Title is not null && Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            return true;
        return Tags is not null
            && Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 关键词完全等于 Category 时返回 true；keyword 为 null/空/空白时返回 false（若作为搜索过滤用途需要上游自行 if (!string.IsNullOrWhiteSpace(...)) 阻挡无效值进入）。
    /// </summary>
    public bool MatchesCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        return Category.Equals(category, StringComparison.OrdinalIgnoreCase);
    }

}
