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
    // 写盘 JSON：缩进可读 + 中文不转义（与引擎原配置序列化选项一致）
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 4,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

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
            Config.StoragePath = MemeDataEngine.DefaultStoragePath();
    }

    // 保存配置到磁盘（容错：失败仅记日志）。
    public async Task SaveConfigAsync()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(Config, JsonOptions);
            await File.WriteAllTextAsync(ConfigPath, json);
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
