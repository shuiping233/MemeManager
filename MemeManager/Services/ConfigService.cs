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

    // 加载配置：读文件反序列化；缺失/为空时用默认存储路径兜底。
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

        // 记录已落盘的配置副本，供 SaveConfigAsync 的值相等比对（避免启动即重复写盘）。
        _lastWrittenConfig = Config with { };
    }

    // 上次已落盘的配置对象；若本次与上次值相等（依赖 AppConfig 的 record 值相等）则跳过写盘，
    // 避免无变化重复 IO（省去每次序列化/反序列化）。
    private AppConfig? _lastWrittenConfig;

    // 保存配置到磁盘（容错：失败仅记日志）。
    // 去重：与上次落盘的配置值相等则跳过写盘（基于 AppConfig record 的字段级值比较）。
    public async Task SaveConfigAsync()
    {
        // record 值相等：所有字段逐一比较，未变化则无需写盘。
        if (_lastWrittenConfig != null && Config == _lastWrittenConfig)
            return;

        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(Config, JsonOptions);
            await File.WriteAllTextAsync(ConfigPath, json);
            _lastWrittenConfig = Config with { }; // 保留一份值副本，后续比较用
        }
        catch (Exception ex)
        {
            Logger.Log($"[Config] 保存配置失败: {ex.Message}");
        }
    }

    // 打补丁并立即持久化（纯配置写入；存储路径合法性校验与数据目录重载由 MemeDataEngine 负责）。
    public async Task UpdateConfigAsync(Action<AppConfig> patch)
    {
        patch(Config);
        await SaveConfigAsync();
    }
}
