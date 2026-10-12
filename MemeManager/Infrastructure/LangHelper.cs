using System.Globalization;

namespace MemeManager.Infrastructure;

// 语言相关逻辑统一收口：系统语言探测 + fallback、配置语言应用、语言代码与下拉索引互转、
// 运行时切换语言入口。支持的语言由软件目录下的 Strings/<lang>/ 子目录自动发现，
// 无需在代码里硬编码语言列表。所有涉及语言更改的地方都只调用这里，避免散落。
public static class LangHelper
{
    public const string SystemLanguage = "System";

    public static IReadOnlyList<string> SupportedLanguages { get; } = DiscoverLanguages();

    public static string DefaultLanguage { get; } =
        SupportedLanguages.FirstOrDefault(l => l.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
        ?? SupportedLanguages.FirstOrDefault()
        ?? "zh-CN";

    // 下拉列表模型：Code 为空字符串表示“跟随系统”，DisplayName 为展示文案。
    // 用可变类而非 record，便于切换语言后原地刷新显示名（避免重设 ItemsSource 触发递归）。
    public class LanguageOption : System.ComponentModel.INotifyPropertyChanged
    {
        private string _displayName = string.Empty;

        public string Code { get; }

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName != value)
                {
                    _displayName = value;
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public LanguageOption(string code, string displayName)
        {
            Code = code;
            _displayName = displayName;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    private static string ResolveLanguageDisplayName(string code)
    {
        var key = $"Settings_Language_{code}";
        var display = Localization.Get(key);
        if (display == key || string.IsNullOrEmpty(display))
        {
            try
            { display = CultureInfo.GetCultureInfo(code).NativeName; }
            catch
            { display = code; }
        }
        return display;
    }

    public static System.Collections.Generic.List<LanguageOption> BuildLanguageOptions()
    {
        var options = new System.Collections.Generic.List<LanguageOption>
        {
            new(SystemLanguage, Localization.Get("Settings_Language_System")),
        };
        foreach (var lang in SupportedLanguages)
        {
            options.Add(new LanguageOption(lang, ResolveLanguageDisplayName(lang)));
        }
        return options;
    }

    public static void RefreshLanguageOptions(IList<LanguageOption> options)
    {
        foreach (var opt in options)
        {
            if (string.IsNullOrEmpty(opt.Code) || opt.Code.Equals(SystemLanguage, StringComparison.OrdinalIgnoreCase))
                opt.DisplayName = Localization.Get("Settings_Language_System");
            else
                opt.DisplayName = ResolveLanguageDisplayName(opt.Code);
        }
    }

    public static string ResolveEffectiveLanguage(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && !configured!.Equals(SystemLanguage, StringComparison.OrdinalIgnoreCase))
            return SupportedLanguages.Contains(configured, StringComparer.OrdinalIgnoreCase) ? configured : DefaultLanguage;

        return DetectSystemLanguage();
    }

    public static string DetectSystemLanguage()
    {
        try
        {
            var sys = Windows.System.UserProfile.GlobalizationPreferences.Languages.Count > 0 ? Windows.System.UserProfile.GlobalizationPreferences.Languages[0] : CultureInfo.CurrentCulture.Name;

            if (Array.Exists(SupportedLanguages.ToArray(), l => l.Equals(sys, StringComparison.OrdinalIgnoreCase)))
                return sys;

            var primary = sys.Split('-')[0];
            foreach (var l in SupportedLanguages)
            {
                if (l.Split('-')[0].Equals(primary, StringComparison.OrdinalIgnoreCase))
                    return l;
            }
        }
        catch { }

        return DefaultLanguage;
    }

    public static string? LangCodeFromIndex(int idx, IList<LanguageOption> options)
    {
        if (idx < 0 || idx >= options.Count)
            return SystemLanguage;
        var code = options[idx].Code;
        return string.IsNullOrEmpty(code) ? SystemLanguage : code;
    }

    public static int IndexFromLangCode(string? code, IList<LanguageOption> options)
    {
        if (string.IsNullOrWhiteSpace(code))
            return 0;
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].Code.Equals(code, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

    public static void SetLanguage(string? code)
    {
        try
        {
            var effective = ResolveEffectiveLanguage(code);
            Localization.Instance?.SetLanguage(effective);
        }
        catch (Exception ex)
        {
            Logger.Log($"[LangHelper] 切换语言失败: {ex.Message}");
        }
    }

    // 扫描 Strings 目录，返回含 Resources.resw 的语言子目录名。
    private static string[] DiscoverLanguages()
    {
        try
        {
            var stringsFolder = Path.Combine(AppContext.BaseDirectory, "Strings");
            if (!Directory.Exists(stringsFolder))
                return Array.Empty<string>();

            return Directory.GetDirectories(stringsFolder)
                .Select(Path.GetFileName)
                .Where(name =>
                        !string.IsNullOrWhiteSpace(name)
                        && File.Exists(Path.Combine(stringsFolder, name!, "Resources.resw")))
                .ToArray()!;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
