namespace MemeManager.Models;

// 当前视图所属的分类类型（MainPage 视图状态）。
// 本文件同时负责：枚举本身、各“虚拟分类”的固定存储名、以及 config 字符串 <-> (kind, 名称) 的互转。
//
// 约定：
//  - Normal（普通磁盘分类）：存储名即文件夹名，无固定虚拟名。
//  - 其它 kind（如 All）为虚拟分类，有固定存储名（见 KindNameMap），与文件夹无关。
//  - 将来新增虚拟分类（如 Recent）：只需在 enum 加一项 + 在 KindNameMap 加一行映射，调用方无需改动。
public enum CategoryKind
{
    Normal = 0,
    All = 1,
}

public static class CategoryKindExtensions
{
    // 各虚拟分类固定的“存储名 / 显示名”。普通分类（Normal）不在此表，直接使用文件夹名。
    // key = kind，value = 该虚拟分类在 config 中保存、以及在 CurrentCategory 中承载的固定名字。
    private static readonly Dictionary<CategoryKind, string> KindNameMap = new()
    {
        [CategoryKind.All] = "AllMemes",
        // [CategoryKind.Recent] = "RecentMemes", // 将来新增虚拟分类时在此加一行
    };

    /// <summary>虚拟分类的固定名（如 All -> "AllMemes"）。普通分类（Normal）无虚拟名，返回 null。</summary>
    public static string? VirtualName(this CategoryKind kind)
        => KindNameMap.TryGetValue(kind, out var name) ? name : null;

    /// <summary>
    /// 将 config 中保存的分类字符串还原为 (kind, 分类名)。
    /// 规则：空串（兼容旧数据）或命中虚拟名 -> 对应虚拟 kind；否则视为普通分类（名字即原串）。
    /// </summary>
    public static (CategoryKind Kind, string Name) Resolve(this string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return (CategoryKind.All, KindNameMap[CategoryKind.All]); // 兼容旧数据：空串 = 全部表情

        foreach (var kv in KindNameMap)
            if (string.Equals(kv.Value, stored, StringComparison.OrdinalIgnoreCase))
                return (kv.Key, kv.Value);

        return (CategoryKind.Normal, stored!);
    }

    /// <summary>
    /// 将 (kind, 分类名) 编码为写入 config 的字符串。
    /// 普通分类直接用真实文件夹名；虚拟分类用其固定名。
    /// </summary>
    public static string ToStored(this CategoryKind kind, string name)
        => kind == CategoryKind.Normal ? name : (KindNameMap.TryGetValue(kind, out var n) ? n : name);
}
