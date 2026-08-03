namespace MemeManager.Infrastructure;

/// <summary>
/// 文件/文件夹名校验（数据一致性与用户体验，**非安全边界**）。
/// 判断 Windows 是否接受该名称作为普通文件夹名：拒绝 "."/".."、路径分隔符、非法字符、
///
/// 职责边界说明：
/// - 设备名（CON/PRN/NUL/COM1-9/LPT1-9 等）**不会引发危险行为**（不会打开控制台/破坏系统），
///   只是 Windows 把它当设备而非普通文件夹名，CreateDirectory 会失败。
/// - 真正的路径逃逸防护（".."、绝对路径、UNC、跨盘）由 SafePath.CombineChildPath /
///   IsSubPathOf 承担（拼完 GetFullPath 后判定），本类不替代它。
/// </summary>
public static class FileNameValidator
{
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

        // Windows 保留设备名：CON、PRN、AUX、NUL、COM1-9、LPT1-9（含 "CON.txt" 这种带扩展名形态）。
        // 非安全边界：仅因 CreateDirectory("...CON") 会失败，提前拦截避免脏状态。
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
