using System.Globalization;

namespace MemeManager.Infrastructure;

// 版本号字符串的解析与比较（更新检查专用）。
// 支持 "v1.2.3" / "1.2.3" / "1.2" / "1.2.3.4" 等纯数字点分形式，以及预发布后缀
// （如 "1.2.3-beta.1"，低于同号正式版）。
// 不依赖 System.Version：后者不支持 v 前缀与预发布后缀。
public readonly struct VersionString : IComparable<VersionString>
{
    private readonly int[] _numbers;
    private readonly string _prerelease; // 空串 = 正式版

    private VersionString(int[] numbers, string prerelease)
    {
        _numbers = numbers;
        _prerelease = prerelease;
    }

    // 解析版本号字符串；失败（空/含非数字段/如本地 dev build 的
    // "2026-07-14 12:00:00 dev build"）返回 false。
    public static bool TryParse(string? s, out VersionString version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        var text = s.Trim();

        // 去掉常见的前导 v/V（如 "v1.2.3"）。要求 v 后紧跟数字，避免误伤纯字母串。
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsDigit(text[1]))
            text = text[1..];

        // 主版本与预发布后缀以 '-' 分隔（取第一个 '-' 之后为后缀）
        string prerelease = string.Empty;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
        }

        var parts = text.Split('.');
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            // NumberStyles.None：只允许纯数字段，不允许正负号/空白
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        version = new VersionString(numbers, prerelease);
        return true;
    }

    // 逐段比较；段数不足补 0（1.2 == 1.2.0）；主版本相同则正式版高于预发布版。
    public int CompareTo(VersionString other)
    {
        int n = Math.Max(_numbers.Length, other._numbers.Length);
        for (int i = 0; i < n; i++)
        {
            int a = i < _numbers.Length ? _numbers[i] : 0;
            int b = i < other._numbers.Length ? other._numbers[i] : 0;
            if (a != b) return a.CompareTo(b);
        }

        // 主版本相同：正式版（无后缀）高于任何预发布版；同为预发布按字典序
        bool hasPre = _prerelease.Length > 0;
        bool otherHasPre = other._prerelease.Length > 0;
        if (hasPre != otherHasPre) return hasPre ? -1 : 1;
        return string.CompareOrdinal(_prerelease, other._prerelease);
    }

    // latest 是否严格新于 current。任一方无法解析（如本地 dev build）一律视为
    // false——不提示更新（自己编译的版本无需被催更；远端数据异常也不误报）。
    public static bool IsNewer(string? latest, string? current)
    {
        if (!TryParse(latest, out var l)) return false;
        if (!TryParse(current, out var c)) return false;
        return l.CompareTo(c) > 0;
    }

    // 当前版本是否为开发版构建（如本地构建的 "2026-07-14 12:00:00 dev build"）。
    // 注意不能靠 TryParse 判定：日期里的 '-' 会被当成预发布分隔符，
    // "2026-07-14 12:00:00 dev build" 会被解析成主版本 2026 + 后缀而"成功"，
    // 误判为合法正式版。此类版本没有语义化版本号：仍可走检查流程拿远端版本号，
    // 但无法判断更新状态。
    public static bool IsDevBuild(string? version)
        => !string.IsNullOrWhiteSpace(version)
           && version.EndsWith("dev build", StringComparison.OrdinalIgnoreCase);
}
