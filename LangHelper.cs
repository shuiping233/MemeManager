using System;
using System.Globalization;
using MemeManager.Helpers;
using MemeManager.Models;

namespace MemeManager;

// 语言相关逻辑统一收口：系统语言探测 + fallback、配置语言应用、语言代码与下拉索引互转、
// 运行时切换语言入口。所有涉及语言更改的地方都只调用这里，避免散落。
public static class LangHelper
{
    // 应用实际支持的语言（resw 里有的）。不在列表里的系统语言统一 fallback 到默认语言。
    public static string[] SupportedLanguages { get; } = { "zh-CN", "en-US" };

    // 默认语言：系统语言不被支持时的兜底。
    public const string DefaultLanguage = "zh-CN";

    // ComboBox 选项顺序：0=跟随系统 1=中文 2=English（与 SettingsPage.xaml 的 ComboBoxItem 对应）。
    public const int IndexSystem = 0;
    public const int IndexChinese = 1;
    public const int IndexEnglish = 2;

    // 配置里 Language 为 null（首次启动）时，返回系统语言（fallback 到默认语言）。
    public static string ResolveEffectiveLanguage(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return SupportedLanguages.Contains(configured, StringComparer.OrdinalIgnoreCase)
                ? configured
                : DefaultLanguage;

        return DetectSystemLanguage();
    }

    // 探测系统语言：取 Windows 显示语言，命中支持列表则返回，否则兜底默认语言。
    public static string DetectSystemLanguage()
    {
        try
        {
            var sys = Windows.System.UserProfile.GlobalizationPreferences.Languages.Count > 0
                ? Windows.System.UserProfile.GlobalizationPreferences.Languages[0]
                : CultureInfo.CurrentCulture.Name;

            // 先精确匹配，再按主语言(如 en / zh)匹配，最后兜底。
            if (Array.Exists(SupportedLanguages, l => l.Equals(sys, StringComparison.OrdinalIgnoreCase)))
                return sys;

            var primary = sys.Split('-')[0];
            foreach (var l in SupportedLanguages)
            {
                if (l.Split('-')[0].Equals(primary, StringComparison.OrdinalIgnoreCase))
                    return l;
            }
        }
        catch
        {
            // 探测失败：保持兜底
        }

        return DefaultLanguage;
    }

    // 下拉索引 -> 语言代码。0(跟随系统) 返回 null，表示“使用系统语言”。
    public static string? LangCodeFromIndex(int idx) => idx switch
    {
        IndexChinese => "zh-CN",
        IndexEnglish => "en-US",
        _ => null,
    };

    // 语言代码(含 null=跟随系统) -> 下拉索引。
    public static int IndexFromLangCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return IndexSystem;
        return code.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ? IndexChinese
            : code.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? IndexEnglish
            : IndexSystem;
    }

    // 应用“配置中的语言”：首次启动(配置为 null)跟随系统，否则用配置值。
    // 在 App 启动、配置读取完毕后立即调用，使主窗口一出来就是正确文案。
    public static void ApplyConfiguredLanguage()
    {
        var effective = ResolveEffectiveLanguage(App.DataEngine.Config.Language);
        SetLanguage(effective);
    }

    // 运行时切换语言：写入配置并立即生效（库支持不重启切换）。
    // 传 null 表示“跟随系统”。
    public static void SetLanguage(string? code)
    {
        try
        {
            var effective = ResolveEffectiveLanguage(code);
            Localization.Instance?.SetLanguage(effective);
            App.DataEngine.Config.Language = code; // 保存用户选择（null=跟随系统）
        }
        catch (Exception ex)
        {
            Logger.Log($"[LangHelper] 切换语言失败: {ex.Message}");
        }
    }
}
