using System;
using System.IO;
using System.Threading.Tasks;
using WinUI3Localizer;

namespace MemeManager.Helpers;

// WinUI3Localizer 封装：从输出目录的 Strings/<lang>/Resources.resw 读取（非 PRI），
// 支持非打包部署、不依赖会崩的 PrimaryLanguageOverride、可运行时切换语言。
public static class Localization
{
    public static ILocalizer? Instance { get; private set; }

    public static async Task InitializeAsync()
    {
        try
        {
            var stringsFolder = Path.Combine(AppContext.BaseDirectory, "Strings");
            var defaultLang = LangHelper.DetectSystemLanguage();
            Instance = await new LocalizerBuilder()
                .AddStringResourcesFolderForLanguageDictionaries(stringsFolder)
                .SetOptions(options => options.DefaultLanguage = defaultLang)
                .Build();
        }
        catch (Exception ex)
        {
            Logger.Log($"[Localization] 初始化失败(将回退 key): {ex.Message}");
        }
    }

    // 取本地化字符串；未初始化或缺失时返回 key 本身，避免界面空白。
    public static string Get(string key) => Instance?.GetLocalizedString(key) ?? key;
}
