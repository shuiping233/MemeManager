namespace MemeManager.Infrastructure;

/// <summary>
/// 文件系统路径边界封装（安全边界）。
/// 原则：任何“用户可控字符串 → 文件系统路径”的构造都必须经过本类，
/// 统一在“Combine + 规范化（GetFullPath）之后”做边界判定，而不是在字符串上做黑名单。
/// 参考：Python pathlib 的 resolve() + startswith 判定；.NET 8+ 的 Path.GetRelativePath 反向判定。
///
/// 职责边界说明：
/// - 本类只回答"路径是否跑出根目录"（逃逸防护），不判断 Windows 喜不喜欢这个名字
///   （设备名/尾随点/非法字符等属 FileNameValidator 的数据一致性职责，非安全边界）。
/// - 两层配合：FileNameValidator 提前拦截给出友好提示，SafePath 在 Combine 后兜底——
///   ".."、绝对路径、UNC、跨盘等无论哪层漏过，最终由本类拒绝。
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
}
