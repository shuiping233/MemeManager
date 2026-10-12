using System.Text.Json;
using MemeManager.Infrastructure;
using MemeManager.Models;

namespace MemeManager.Services;

// 应用配置管理（从 MemeDataEngine 拆出）：持有 AppConfig、负责配置文件的加载/保存与打补丁持久化。
// 与“图片数据/元数据”解耦——本类不碰任何 meme 缓存，仅管应用设置（StoragePath / 主题 / 热键 / 语言等）。
// 数据目录（_baseDir）的解析与元数据重载仍属 MemeDataEngine 职责，不在本类。
public class ConfigService
{
    private static JsonSerializerOptions JsonOptions => AppConstants.JsonOptions;

    public AppConfig Config { get; set; } = new();

    // 配置文件固定保存在 %LOCALAPPDATA% 下（与数据目录解耦），否则迁移数据目录后二次启动读不到配置
    private static string ConfigDir => AppConstants.AppDataDirPath;
    private static string ConfigPath => AppConstants.ConfigPath;

    public void LoadConfig()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);

            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                    if (cfg != null) Config = cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Config] 读取配置失败: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(Config.StoragePath))
            Config.StoragePath = AppConstants.DefaultMemeDataStoragePath();

        _lastWrittenConfig = Config with { };
    }

    private AppConfig? _lastWrittenConfig;

    public async Task SaveConfigAsync()
    {
        if (_lastWrittenConfig != null && Config == _lastWrittenConfig)
            return;

        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(Config, JsonOptions);
            await File.WriteAllTextAsync(ConfigPath, json);
            _lastWrittenConfig = Config with { };
        }
        catch (Exception ex)
        {
            Logger.Log($"[Config] 保存配置失败: {ex.Message}");
        }
    }

    public async Task UpdateConfigAsync(Action<AppConfig> patch)
    {
        patch(Config);
        await SaveConfigAsync();
    }
}
