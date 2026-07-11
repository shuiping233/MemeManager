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
    // 根目录：MeMeManagerData
    private string _baseDir;

    // 内存缓存
    private readonly List<MemeModel> _memeCache = new();

    // 🎯 标题反查 Map：title(小写) -> 该 title 对应的文件名列表（哈希文件名）
    // 便于按标题秒级检索
    private readonly Dictionary<string, List<string>> _titleReverseMap = new(StringComparer.OrdinalIgnoreCase);

    public string BaseDir => _baseDir;
    public AppConfig Config { get; private set; } = new();

    public MemeDataEngine()
    {
        _baseDir = DefaultStoragePath();
        Config.StoragePath = _baseDir;
    }

    public static string DefaultStoragePath()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Path.Combine(pictures, "MeMeManagerData");
    }

    // ---------- 配置 ----------

    public async Task InitializeAsync()
    {
        LoadConfig();

        _baseDir = string.IsNullOrWhiteSpace(Config.StoragePath) ? DefaultStoragePath() : Config.StoragePath;
        Directory.CreateDirectory(_baseDir);

        await LoadAllMetadataAsync();
    }

    private string ConfigPath => Path.Combine(_baseDir, "config.json");

    private void LoadConfig()
    {
        try
        {
            Directory.CreateDirectory(_baseDir);
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var cfg = JsonSerializer.Deserialize(json, MemeJsonContext.Default.AppConfig);
                    if (cfg != null) Config = cfg;
                }
            }
        }
        catch (Exception ex)
        {
            MemeManager.Logger.Log($"[Engine] 读取配置失败: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(Config.StoragePath))
            Config.StoragePath = DefaultStoragePath();
    }

    public async Task SaveConfigAsync()
    {
        try
        {
            Directory.CreateDirectory(_baseDir);
            var json = JsonSerializer.Serialize(Config, MemeJsonContext.Default.AppConfig);
            await File.WriteAllTextAsync(ConfigPath, json);
        }
        catch (Exception ex)
        {
            MemeManager.Logger.Log($"[Engine] 保存配置失败: {ex.Message}");
        }
    }

    public async Task UpdateConfigAsync(Action<AppConfig> patch)
    {
        patch(Config);
        await SaveConfigAsync();
        if (!string.IsNullOrWhiteSpace(Config.StoragePath) &&
            !Config.StoragePath.Equals(_baseDir, StringComparison.OrdinalIgnoreCase))
        {
            _baseDir = Config.StoragePath;
            await InitializeAsync();
        }
    }

    // ---------- 加载 ----------

    private async Task LoadAllMetadataAsync()
    {
        _memeCache.Clear();
        _titleReverseMap.Clear();

        var dirs = Directory.GetDirectories(_baseDir);
        foreach (var dir in dirs)
        {
            var category = Path.GetFileName(dir);
            var metaPath = Path.Combine(dir, ".metadata.json");
            CategoryMetadata meta;
            if (File.Exists(metaPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(metaPath);
                    meta = JsonSerializer.Deserialize(json, MemeJsonContext.Default.CategoryMetadata)
                           ?? new CategoryMetadata();
                }
                catch
                {
                    meta = new CategoryMetadata();
                }
            }
            else
            {
                meta = new CategoryMetadata();
            }

            foreach (var kv in meta.Items)
            {
                var fileName = kv.Key;
                var localPath = Path.Combine(dir, fileName);
                if (!File.Exists(localPath)) continue;

                var hash = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);

                var model = new MemeModel
                {
                    Hash = hash,
                    Extension = ext,
                    LocalPath = localPath,
                    Category = category,
                    Title = kv.Value.Title,
                    Tags = kv.Value.Tags ?? new List<string>()
                };
                _memeCache.Add(model);
                IndexTitle(model);
            }
        }
    }

    private void IndexTitle(MemeModel meme)
    {
        if (string.IsNullOrWhiteSpace(meme.Title)) return;
        if (!_titleReverseMap.TryGetValue(meme.Title, out var list))
        {
            list = new List<string>();
            _titleReverseMap[meme.Title] = list;
        }
        list.Add(meme.FileName);
    }

    // ---------- 查询 ----------

    public IReadOnlyList<MemeModel> GetMemes(string? category = null, string? keyword = null)
    {
        IEnumerable<MemeModel> query = _memeCache;

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(m => m.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(m =>
                (m.Title != null && m.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                m.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        }

        return query.OrderByDescending(m => m.DateAdded).ToList();
    }

    public IReadOnlyList<string> GetCategories()
    {
        // 分类 = 内存中已有分类 ∪ 磁盘上实际存在的分类文件夹
        var set = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _memeCache)
            if (!string.IsNullOrWhiteSpace(m.Category)) set.Add(m.Category);

        if (Directory.Exists(_baseDir))
        {
            foreach (var dir in Directory.GetDirectories(_baseDir))
            {
                // 仅将含有 .metadata.json 的文件夹视为有效分类
                if (File.Exists(Path.Combine(dir, ".metadata.json")))
                    set.Add(Path.GetFileName(dir));
            }
        }

        return set.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // 通过标题反查文件名列表
    public IReadOnlyList<string> ReverseLookupByTitle(string title)
    {
        if (_titleReverseMap.TryGetValue(title, out var list))
            return list;
        return new List<string>();
    }

    // ---------- 导入 ----------

    public async Task<MemeModel?> ImportMemeAsync(string sourcePath, string category, string? title = null, List<string>? tags = null)
    {
        if (!File.Exists(sourcePath)) return null;

        try
        {
            string hash = await CalculateSha256Async(sourcePath);
            string ext = Path.GetExtension(sourcePath);
            string fileName = $"{hash}{ext}";

            // 去重：同分类下文件名已存在则视为重复
            var categoryDir = Path.Combine(_baseDir, SanitizeCategory(category));
            var targetPath = Path.Combine(categoryDir, fileName);
            var existing = _memeCache.FirstOrDefault(m =>
                m.Category.Equals(category, StringComparison.OrdinalIgnoreCase) && m.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            Directory.CreateDirectory(categoryDir);
            await Task.Run(() => File.Copy(sourcePath, targetPath, overwrite: true));

            var meta = await LoadCategoryMetadataAsync(categoryDir);

            var model = new MemeModel
            {
                Hash = hash,
                Extension = ext,
                LocalPath = targetPath,
                Category = Path.GetFileName(categoryDir),
                Title = title ?? Path.GetFileNameWithoutExtension(sourcePath),
                Tags = tags ?? new List<string>(),
                DateAdded = DateTime.UtcNow,
                UsageCount = 0
            };

            meta.Items[fileName] = new MemeMetaEntry
            {
                Title = model.Title,
                Tags = model.Tags
            };

            _memeCache.Add(model);
            IndexTitle(model);
            await SaveCategoryMetadataAsync(categoryDir, meta);
            return model;
        }
        catch (Exception ex)
        {
            MemeManager.Logger.Log($"[Engine] 导入表情包失败: {ex.Message}");
            return null;
        }
    }

    // ---------- 导出 ----------

    public async Task ExportMemesAsync(IEnumerable<MemeModel> memes, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var meme in memes)
        {
            if (!File.Exists(meme.LocalPath)) continue;
            var dest = Path.Combine(targetDir, meme.FileName);
            await Task.Run(() => File.Copy(meme.LocalPath, dest, overwrite: true));
        }
    }

    // ---------- 移动到其他分类 ----------

    /// <summary>
    /// 将一批表情移动到目标分类：移动物理文件、更新两分类的 metadata 与内存缓存。
    /// 若目标分类不存在会自动创建。
    /// </summary>
    public async Task MoveMemesToCategoryAsync(IEnumerable<MemeModel> memes, string targetCategory)
    {
        var safeTarget = SanitizeCategory(targetCategory);
        var targetDir = Path.Combine(_baseDir, safeTarget);
        Directory.CreateDirectory(targetDir);
        var targetMeta = await LoadCategoryMetadataAsync(targetDir);

        foreach (var meme in memes)
        {
            if (meme.Category.Equals(safeTarget, StringComparison.OrdinalIgnoreCase))
                continue; // 已在目标分类，跳过

            var sourceDir = Path.Combine(_baseDir, SanitizeCategory(meme.Category));
            var sourceMeta = await LoadCategoryMetadataAsync(sourceDir);

            var destPath = Path.Combine(targetDir, meme.FileName);
            try
            {
                if (File.Exists(meme.LocalPath))
                    File.Move(meme.LocalPath, destPath, overwrite: true);
            }
            catch (Exception ex)
            {
                MemeManager.Logger.Log($"[Engine] 移动文件失败 {meme.FileName}: {ex.Message}");
                continue;
            }

            // 更新目标分类 metadata
            targetMeta.Items[meme.FileName] = new MemeMetaEntry
            {
                Title = meme.Title,
                Tags = meme.Tags
            };

            // 从源分类 metadata 移除
            sourceMeta.Items.Remove(meme.FileName);
            await SaveCategoryMetadataAsync(sourceDir, sourceMeta);

            // 更新内存缓存
            meme.Category = safeTarget;
            meme.LocalPath = destPath;
        }

        await SaveCategoryMetadataAsync(targetDir, targetMeta);
    }

    // ---------- 删除 ----------

    public async Task DeleteMemesAsync(IEnumerable<MemeModel> memes)
    {
        var byCategory = memes.GroupBy(m => m.Category, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byCategory)
        {
            var categoryDir = Path.Combine(_baseDir, SanitizeCategory(group.Key));
            var meta = await LoadCategoryMetadataAsync(categoryDir);
            foreach (var meme in group)
            {
                try { if (File.Exists(meme.LocalPath)) File.Delete(meme.LocalPath); } catch { }
                meta.Items.Remove(meme.FileName);
                _memeCache.Remove(meme);
                if (!string.IsNullOrWhiteSpace(meme.Title) &&
                    _titleReverseMap.TryGetValue(meme.Title, out var list))
                {
                    list.Remove(meme.FileName);
                    if (list.Count == 0) _titleReverseMap.Remove(meme.Title);
                }
            }
            await SaveCategoryMetadataAsync(categoryDir, meta);
        }
    }

    // ---------- 分类管理 ----------

    public async Task<bool> AddCategoryAsync(string category)
    {
        var dir = Path.Combine(_baseDir, SanitizeCategory(category));
        if (Directory.Exists(dir)) return false;
        Directory.CreateDirectory(dir);
        await SaveCategoryMetadataAsync(dir, new CategoryMetadata());
        MemeManager.Logger.Log($"[Engine] 创建分类: {category}");
        return true;
    }

    // ---------- 分类删除 ----------

    public async Task<bool> DeleteCategoryAsync(string category)
    {
        var dir = Path.Combine(_baseDir, SanitizeCategory(category));
        if (!Directory.Exists(dir)) return false;

        try
        {
            // 1. 从内存缓存移除该分类下所有表情，并清理标题反查 Map
            var toRemove = _memeCache.Where(m => m.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var m in toRemove)
            {
                _memeCache.Remove(m);
                if (!string.IsNullOrWhiteSpace(m.Title) &&
                    _titleReverseMap.TryGetValue(m.Title, out var list))
                {
                    list.Remove(m.FileName);
                    if (list.Count == 0) _titleReverseMap.Remove(m.Title);
                }
            }

            // 2. 删除整个分类文件夹（图片 + .metadata.json）
            Directory.Delete(dir, recursive: true);
            MemeManager.Logger.Log($"[Engine] 删除分类: {category}");
            return true;
        }
        catch (Exception ex)
        {
            MemeManager.Logger.Log($"[Engine] 删除分类失败: {ex.Message}");
            return false;
        }
    }

    // ---------- metadata 读写 ----------

    private async Task<CategoryMetadata> LoadCategoryMetadataAsync(string categoryDir)
    {
        var metaPath = Path.Combine(categoryDir, ".metadata.json");
        if (!File.Exists(metaPath)) return new CategoryMetadata();
        try
        {
            var json = await File.ReadAllTextAsync(metaPath);
            return JsonSerializer.Deserialize(json, MemeJsonContext.Default.CategoryMetadata)
                   ?? new CategoryMetadata();
        }
        catch
        {
            return new CategoryMetadata();
        }
    }

    private async Task SaveCategoryMetadataAsync(string categoryDir, CategoryMetadata meta)
    {
        Directory.CreateDirectory(categoryDir);
        var metaPath = Path.Combine(categoryDir, ".metadata.json");
        var json = JsonSerializer.Serialize(meta, MemeJsonContext.Default.CategoryMetadata);
        await File.WriteAllTextAsync(metaPath, json);
    }

    // ---------- 工具 ----------

    private static string SanitizeCategory(string category)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(category.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "未分类" : cleaned;
    }

    private static async Task<string> CalculateSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        byte[] hashBytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public async Task IncrementUsageAsync(string hash)
    {
        var meme = _memeCache.FirstOrDefault(m => m.Hash == hash);
        if (meme != null) meme.UsageCount++;
    }
}
