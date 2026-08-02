using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MemeManager.Infrastructure;
using MemeManager.Models;
using MemeManager.Views;

namespace MemeManager.Services;

// 应用配置管理（从 MemeDataEngine 拆出）：持有 AppConfig、负责配置文件的加载/保存与打补丁持久化。
// 与“图片数据/元数据”解耦——本类不碰任何 meme 缓存，仅管应用设置（StoragePath / 主题 / 热键 / 语言等）。
// 数据目录（_baseDir）的解析与元数据重载仍属 MemeDataEngine 职责，不在本类。
public class ConfigService
{
    private static JsonSerializerOptions JsonOptions => AppConstants.JsonOptions;

    public AppConfig Config { get; set; } = new();

    // 配置文件固定保存在 %LOCALAPPDATA% 下（与数据目录解耦），否则迁移数据目录后二次启动读不到配置
    private static string ConfigDir => AppConstants.DefaultAppDataDir;
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
            Config.StoragePath = Utils.DefaultDataStoragePath();
    }

    // 上次已落盘的 JSON 内容；若本次序列化结果与它相同则跳过写盘，避免无变化重复 IO。
    private string? _lastWrittenJson;

    // 保存配置到磁盘（容错：失败仅记日志）。
    // 去重：序列化后与上次落盘内容一致则不写盘（Config 为引用类型，patch 后内容可能未变）。
    public async Task SaveConfigAsync()
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(Config, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Config] 序列化配置失败: {ex.Message}");
            return;
        }

        if (string.Equals(json, _lastWrittenJson, StringComparison.Ordinal))
            return;

        try
        {
            Directory.CreateDirectory(ConfigDir);
            await File.WriteAllTextAsync(ConfigPath, json);
            _lastWrittenJson = json;
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
