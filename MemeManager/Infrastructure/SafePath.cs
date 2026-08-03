using System;

namespace MemeManager.Infrastructure;

/// <summary>
/// 文件系统路径边界封装（安全审计修复的核心基础设施）。
/// 原则：任何“用户可控字符串 → 文件系统路径”的构造都必须经过本类，
/// 统一在“Combine + 规范化（GetFullPath）之后”做边界判定，而不是在字符串上做黑名单。
/// 参考：Python pathlib 的 resolve() + startswith 判定；.NET 8+ 的 Path.GetRelativePath 反向判定。
/// </summary>
public static class SafePath
{
    /// <summary>
    /// 把 child 拼到 parent 下，规范化后仍必须在 parent 之内，否则抛 InvalidOperationException。
    /// 覆盖的逃逸形态：child 含 ".."、child 是绝对路径、child 以盘符/UNC 开头等
    /// （Path.Combine 在这些情况下都不会乖乖拼在 parent 下，必须拼完再判定）。
    /// </summary>
    public static string CombineChildPath(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(parent))
            throw new ArgumentException("parent 不能为空", nameof(parent));
        if (string.IsNullOrWhiteSpace(child))
            throw new ArgumentException("child 不能为空", nameof(child));

        var fullPath = Path.GetFullPath(Path.Combine(parent, child));
        if (!IsSubPathOf(parent, fullPath))
            throw new InvalidOperationException($"Path escape detected: child='{child}' parent='{parent}'");
        return fullPath;
    }

    /// <summary>
    /// candidate 是否为 basePath 的严格子路径（规范化后比较）。
    /// 返回 false 的情况：candidate == basePath、跨盘（Path.GetRelativePath 返回绝对路径）、
    /// 以 ".." 逃逸到 basePath 之外。
    /// 注意：不能简单用 relative.StartsWith("..")——合法文件名如 "..hidden" 的相对路径
    /// 是 "..hidden"，会被误判为逃逸；必须精确匹配 ".." 或以 ".." + 分隔符开头。
    /// </summary>
    public static bool IsSubPathOf(string basePath, string candidate)
    {
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(candidate))
            return false;

        string fullBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));
        string fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        // 等于 base 本身不算“子”路径
        if (fullCandidate.Equals(fullBase, StringComparison.OrdinalIgnoreCase))
            return false;

        string relative = Path.GetRelativePath(fullBase, fullCandidate);

        // 跨盘：GetRelativePath 返回绝对路径（如 "D:\x"）
        if (Path.IsPathFullyQualified(relative))
            return false;

        // 精确逃逸判定：relative == ".." 或以 "..\" / "../" 开头
        if (relative.Equals("..", StringComparison.OrdinalIgnoreCase))
            return false;
        if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// 分类名严格校验：必须是单个文件夹名（非空、非 "."/".."、无路径分隔符与非法字符、
    /// 无尾随点/空格、非 Windows 保留设备名）。
    /// 注意：不 Trim——调用方必须先 Trim 再传入，否则 "abc " 这类尾随空格会被 Windows
    /// 静默吞掉（建出 "abc"），导致磁盘目录名与分类名不一致；前导空格合法（Windows 保留）。
    /// 分类不是路径，用户若想分层应建多个分类，而非输入 "旅行/2026"。
    /// </summary>
    public static bool IsValidCategoryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // "." / ".."：目录逃逸根子（".. " 之类尾随空格形态由下方 EndsWith 拦截）
        if (name is "." or "..")
            return false;

        // 尾随点/空格：Windows 会静默吞掉（CreateDirectory("abc.") 实际建 "abc"），
        // 导致磁盘目录名与分类名不一致，后续删除/重命名/打开全部错位。
        if (name.EndsWith('.') || name.EndsWith(' '))
            return false;

        // 非法字符（含 / \ : * ? " < > | 与控制字符）
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        // Windows 保留设备名：CON、PRN、AUX、NUL、COM1-9、LPT1-9（含 "CON.txt" 这种带扩展名形态）
        string stem = name.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem))
            return false;

        return true;
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
}
