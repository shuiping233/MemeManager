using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using MemeManager.Models;

namespace MemeManager.Data;

public class MemeDataEngine
{
    private readonly string _baseDir;
    private readonly string _imagesDir;
    private readonly string _dbFilePath;
    
    // 内存缓存，用于极速渲染 UI，避免频繁读盘
    private readonly List<MemeModel> _memeCache = new();

    public MemeDataEngine()
    {
        // 1. 初始化存储路径（这里定在本地 AppData 下，也可以改成 AppDomain.CurrentDomain.BaseDirectory）
        _baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemeManager");
        _imagesDir = Path.Combine(_baseDir, "Images");
        _dbFilePath = Path.Combine(_baseDir, "memes.json");

        // 确保目录存在
        Directory.CreateDirectory(_imagesDir);
    }

    /// <summary>
    /// 初始化加载：程序启动时，异步从 json 中恢复数据到内存缓存
    /// </summary>
    public async Task InitializeAsync()
    {
        _memeCache.Clear();

        if (!File.Exists(_dbFilePath))
        {
            await SaveDatabaseAsync();
            return;
        }

        try
        {
            string jsonText = await File.ReadAllTextAsync(_dbFilePath);
            if (!string.IsNullOrWhiteSpace(jsonText))
            {
                // 🎯 严格遵循 AOT 安全：传入编译期生成的 ListMemeModel 元数据
                var list = JsonSerializer.Deserialize(jsonText, MemeJsonContext.Default.ListMemeModel);
                if (list != null)
                {
                    _memeCache.AddRange(list);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Engine] 读取数据库失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 导入新表情包：计算哈希、拷贝文件、规避重复、入库
    /// </summary>
    public async Task<MemeModel?> ImportMemeAsync(string sourcePath, string category, List<string>? tags = null)
    {
        if (!File.Exists(sourcePath)) return null;

        try
        {
            // 1. 计算 SHA256 哈希值作为唯一 ID
            string hash = await CalculateSha256Async(sourcePath);

            // 2. 检查去重：如果哈希已存在，直接返回已有的模型，防止硬盘爆满
            var existing = _memeCache.FirstOrDefault(m => m.Hash == hash);
            if (existing != null) return existing;

            // 3. 确定新文件的拷贝目标路径（保持原有后缀）
            string ext = Path.GetExtension(sourcePath);
            string targetFileName = $"{hash}{ext}";
            string targetPath = Path.Combine(_imagesDir, targetFileName);

            // 4. 拷贝文件到托管目录
            await Task.Run(() => File.Copy(sourcePath, targetPath, overwrite: true));

            // 5. 构造新模型
            var newMeme = new MemeModel
            {
                Hash = hash,
                LocalPath = targetPath,
                Category = string.IsNullOrWhiteSpace(category) ? "未分类" : category,
                Tags = tags ?? new List<string>(),
                DateAdded = DateTime.UtcNow,
                UsageCount = 0
            };

            // 6. 更新内存并写入本地 JSON
            _memeCache.Add(newMeme);
            await SaveDatabaseAsync();

            return newMeme;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Engine] 导入表情包失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取当前所有表情（提供给 UI 渲染）
    /// </summary>
    public IReadOnlyList<MemeModel> GetMemes(string? category = null, string? keyword = null)
    {
        IEnumerable<MemeModel> query = _memeCache;

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(m => m.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(m => m.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        }

        // 按添加时间倒序排列（新导入的在前面）
        return query.OrderByDescending(m => m.DateAdded).ToList();
    }

    /// <summary>
    /// 增加使用频次（每次点击增加，用于后续做热度推荐）
    /// </summary>
    public async Task IncrementUsageAsync(string hash)
    {
        var meme = _memeCache.FirstOrDefault(m => m.Hash == hash);
        if (meme != null)
        {
            meme.UsageCount++;
            await SaveDatabaseAsync();
        }
    }

    /// <summary>
    /// 核心私有方法：序列化保存本地
    /// </summary>
    private async Task SaveDatabaseAsync()
    {
        // 🎯 严格遵循 AOT 安全：使用编译期生成的元数据
        string jsonText = JsonSerializer.Serialize(_memeCache, MemeJsonContext.Default.ListMemeModel);
        await File.WriteAllTextAsync(_dbFilePath, jsonText);
    }

    /// <summary>
    /// 异步计算文件 SHA256 哈希值
    /// </summary>
    private static async Task<string> CalculateSha256Async(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        // 使用 .NET 10 的静态哈希方法，免去创建 HashAlgorithm 实例的开销
        byte[] hashBytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}