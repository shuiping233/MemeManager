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
    private string _baseDir;

    private readonly List<MemeModel> _memeCache = new();

    // 标题反查 Map：title(小写) -> 该 title 对应的文件名列表
    private readonly Dictionary<string, List<string>> _titleReverseMap = new(StringComparer.OrdinalIgnoreCase);

    // 分类顺序：分类名(小写, 即文件夹名) -> 优先级（值越大越靠前）
    private readonly Dictionary<string, uint> _categoryOrder = new(StringComparer.OrdinalIgnoreCase);

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

        await LoadCategoryOrderAsync();
        await LoadAllMetadataAsync();
    }

    // 配置文件固定保存在 %LOCALAPPDATA% 下（与数据目录解耦），否则迁移数据目录后二次启动读不到配置
    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemeManager");
    private string ConfigPath => Path.Combine(ConfigDir, "config.json");

    // 分类顺序文件位于“数据保存目录/.metadata.json”（与分类子文件夹内的 .metadata.json 不同层级）
    private string CategoryOrderPath => Path.Combine(_baseDir, ".metadata.json");

    private void LoadConfig()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);

            // 迁移兼容：首次升级时旧 config.json 位于数据目录下，搬到用户目录，避免丢失（含 StoragePath）
            var oldConfigPath = Path.Combine(_baseDir, "config.json");
            if (!File.Exists(ConfigPath) && File.Exists(oldConfigPath))
            {
                try
                {
                    File.Copy(oldConfigPath, ConfigPath, overwrite: true);
                    MemeManager.Logger.Log($"[Engine] 已迁移旧配置到用户目录: {oldConfigPath} -> {ConfigPath}");
                }
                catch (Exception ex)
                {
                    MemeManager.Logger.Log($"[Engine] 迁移旧配置失败: {ex.Message}");
                }
            }

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
            Directory.CreateDirectory(ConfigDir);
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

        // 先把 _baseDir 切到目标路径，确保 config.json 写到“新路径”而不是旧路径
        string newBase = string.IsNullOrWhiteSpace(Config.StoragePath)
            ? DefaultStoragePath()
            : Config.StoragePath;
        bool changed = !newBase.Equals(_baseDir, StringComparison.OrdinalIgnoreCase);
        _baseDir = newBase;

        await SaveConfigAsync();

        // 仅当存放路径真正变化时才重新加载该路径下的元数据
        if (changed)
            await LoadAllMetadataAsync();
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
                    Tags = kv.Value.Tags ?? new List<string>(),
                    Priority = kv.Value.Priority
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

    public IReadOnlyList<MemeModel> GetAllMemes() => _memeCache.ToList();

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

        // Priority 值越大越靠前（左侧/开头）；同值按导入时间新→旧
        return query.OrderByDescending(m => m.Priority).ThenByDescending(m => m.DateAdded).ToList();
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

        var result = set.ToList();
        // 按优先级降序（值越大越靠前），同优先级按名称稳定排序
        result.Sort((a, b) =>
        {
            int pa = _categoryOrder.TryGetValue(a, out var va) ? (int)va : 0;
            int pb = _categoryOrder.TryGetValue(b, out var vb) ? (int)vb : 0;
            int cmp = pb.CompareTo(pa);
            return cmp != 0 ? cmp : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    // ---------- 分类顺序（拖拽重排） ----------

    private async Task LoadCategoryOrderAsync()
    {
        _categoryOrder.Clear();
        try
        {
            if (File.Exists(CategoryOrderPath))
            {
                var json = await File.ReadAllTextAsync(CategoryOrderPath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var meta = JsonSerializer.Deserialize(json, MemeJsonContext.Default.CategoryOrderMetadata);
                    if (meta?.Categories != null)
                        foreach (var kv in meta.Categories)
                            _categoryOrder[kv.Key] = kv.Value.Priority;
                }
            }
        }
        catch (Exception ex)
        {
            MemeManager.Logger.Log($"[Engine] 读取分类顺序失败: {ex.Message}");
        }
    }

    private async Task SaveCategoryOrderAsync()
    {
        try
        {
            Directory.CreateDirectory(_baseDir);
            var meta = new CategoryOrderMetadata
            {
                Categories = _categoryOrder.ToDictionary(
                    kv => kv.Key,
                    kv => new CategoryOrderEntry { Priority = kv.Value })
            };
            var json = JsonSerializer.Serialize(meta, MemeJsonContext.Default.CategoryOrderMetadata);
            await File.WriteAllTextAsync(CategoryOrderPath, json);
        }
        catch (Exception ex)
        {
            MemeManager.Logger.Log($"[Engine] 保存分类顺序失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 按给定分类名顺序整体重算优先级（列表最前=最大优先级，依次递减），写回 .metadata.json。
    /// </summary>
    public async Task ReorderCategoriesAsync(IReadOnlyList<string> orderedNames)
    {
        uint p = (uint)orderedNames.Count;
        foreach (var name in orderedNames)
            _categoryOrder[name] = p--;
        await SaveCategoryOrderAsync();
    }

    // 通过标题反查文件名列表
    public IReadOnlyList<string> ReverseLookupByTitle(string title)
    {
        if (_titleReverseMap.TryGetValue(title, out var list))
            return list;
        return new List<string>();
    }

    // ---------- 导入 ----------

    public async Task<(MemeModel? model, bool duplicate)> ImportMemeAsync(string sourcePath, string category, string? title = null, List<string>? tags = null)
    {
        if (!File.Exists(sourcePath)) return (null, false);

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
            if (existing != null)
            {
                return (existing, true);
            }

            Directory.CreateDirectory(categoryDir);
            await Task.Run(() => File.Copy(sourcePath, targetPath, overwrite: true));

            var meta = await LoadCategoryMetadataAsync(categoryDir);

            // 新导入图片的优先级 = 当前分类已有最大优先级 + 1（后导入排后面）
            uint maxPriority = 0;
            foreach (var entry in meta.Items.Values)
                if (entry.Priority > maxPriority) maxPriority = entry.Priority;

            var model = new MemeModel
            {
                Hash = hash,
                Extension = ext,
                LocalPath = targetPath,
                Category = Path.GetFileName(categoryDir),
                Title = title ?? Path.GetFileNameWithoutExtension(sourcePath),
                Tags = tags ?? new List<string>(),
                DateAdded = DateTime.UtcNow,
                UsageCount = 0,
                Priority = maxPriority + 1
            };

            meta.Items[fileName] = new MemeMetaEntry
            {
                Title = model.Title,
                Tags = model.Tags,
                Priority = model.Priority
            };

            _memeCache.Add(model);
            IndexTitle(model);
            await SaveCategoryMetadataAsync(categoryDir, meta);
            return (model, false);
        }
        catch (Exception ex)
        {
            MemeManager.Logger.Log($"[Engine] 导入表情包失败: {ex.Message}");
            return (null, false);
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

    // ---------- 重命名分类 ----------

    /// <summary>
    /// 重命名分类：重命名对应的物理文件夹，并更新该分类下所有表情的
    /// Category 与 LocalPath（路径中的目录部分），以及 Config.LastCategory。
    /// </summary>
    public async Task<bool> RenameCategoryAsync(string oldName, string newName)
    {
        var safeOld = SanitizeCategory(oldName);
        var safeNew = SanitizeCategory(newName);
        if (string.Equals(safeOld, safeNew, StringComparison.OrdinalIgnoreCase))
            return false;

        var oldDir = Path.Combine(_baseDir, safeOld);
        var newDir = Path.Combine(_baseDir, safeNew);
        if (!Directory.Exists(oldDir)) return false;
        if (Directory.Exists(newDir)) return false; // 目标已存在，避免覆盖

        try
        {
            Directory.Move(oldDir, newDir);
        }
        catch (Exception ex)
        {
            MemeManager.Logger.Log($"[Engine] 重命名分类文件夹失败 {oldName}->{newName}: {ex.Message}");
            return false;
        }

        // 更新内存缓存中该分类下表情的路径与分类名
        foreach (var m in _memeCache)
        {
            if (m.Category.Equals(safeOld, StringComparison.OrdinalIgnoreCase))
            {
                m.Category = safeNew;
                m.LocalPath = Path.Combine(newDir, m.FileName);
            }
        }

        // 若当前记录的上次分类是被重命名的，同步更新
        if (Config.LastCategory.Equals(safeOld, StringComparison.OrdinalIgnoreCase))
        {
            Config.LastCategory = safeNew;
            await SaveConfigAsync();
        }

        // 同步更新分类顺序表中的 key（保留原优先级）
        if (_categoryOrder.TryGetValue(safeOld, out var prio))
        {
            _categoryOrder.Remove(safeOld);
            _categoryOrder[safeNew] = prio;
            await SaveCategoryOrderAsync();
        }

        MemeManager.Logger.Log($"[Engine] 重命名分类: {oldName} -> {newName}");
        return true;
    }

    // ---------- 重排（拖拽调整顺序） ----------

    /// <summary>
    /// 按给定文件名顺序（已是目标顺序）整体重算该分类的 Priority 为 1,2,3...
    /// 并写回 metadata 与内存缓存。
    /// </summary>
    public async Task ReorderMemesAsync(string category, IReadOnlyList<string> orderedFileNames)
    {
        var dir = Path.Combine(_baseDir, SanitizeCategory(category));
        var meta = await LoadCategoryMetadataAsync(dir);

        // 按给定顺序整体重算：列表最前（索引0）拿最大优先级，依次递减，
        // 以契合“Priority 越大越靠前（左）”的展示规则
        uint p = (uint)orderedFileNames.Count;
        foreach (var fileName in orderedFileNames)
        {
            if (meta.Items.TryGetValue(fileName, out var entry))
                entry.Priority = p--;
        }
        // 兜底：列表中未涵盖的 item（理论上不应出现），顺延补上更小的值
        uint tail = 0;
        foreach (var kv in meta.Items)
            if (kv.Value.Priority == 0 && !orderedFileNames.Contains(kv.Key))
                kv.Value.Priority = tail++;

        await SaveCategoryMetadataAsync(dir, meta);

        foreach (var m in _memeCache)
        {
            if (m.Category.Equals(category, StringComparison.OrdinalIgnoreCase) &&
                meta.Items.TryGetValue(m.FileName, out var e2))
            {
                m.Priority = e2.Priority;
            }
        }
    }

    // ---------- 重命名（仅改 metadata 里的 title） ----------

    public async Task RenameMemeAsync(MemeModel meme, string newTitle)
    {
        var title = (newTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Equals(meme.Title, StringComparison.OrdinalIgnoreCase))
            return;

        var dir = Path.Combine(_baseDir, SanitizeCategory(meme.Category));
        var meta = await LoadCategoryMetadataAsync(dir);
        if (meta.Items.TryGetValue(meme.FileName, out var entry))
            entry.Title = title;

        await SaveCategoryMetadataAsync(dir, meta);

        // 更新内存缓存与标题反查表
        if (_titleReverseMap.TryGetValue(meme.Title, out var list))
        {
            list.Remove(meme.FileName);
            if (list.Count == 0) _titleReverseMap.Remove(meme.Title);
        }
        meme.Title = title;
        IndexTitle(meme);
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
        // 新分类默认优先级 0（排在同优先级最后），并持久化顺序
        _categoryOrder[category] = 0;
        await SaveCategoryOrderAsync();
        MemeManager.Logger.Log($"[Engine] 创建分类: {category}");
        return true;
    }

    // 同步确保存在 Default 分类（供 UI 线程的 LoadCategories 调用，避免 async 死锁）
    public void EnsureDefaultCategory()
    {
        var dir = Path.Combine(_baseDir, SanitizeCategory("Default"));
        if (Directory.Exists(dir)) return;
        Directory.CreateDirectory(dir);
        try
        {
            var metaPath = Path.Combine(dir, ".metadata.json");
            if (!File.Exists(metaPath))
                File.WriteAllTextAsync(metaPath,
                    JsonSerializer.Serialize(new CategoryMetadata(), MemeJsonContext.Default.CategoryMetadata))
                    .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MemeManager.Logger.Log($"[Engine] 创建默认分类失败: {ex.Message}");
        }
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

            // 3. 从分类顺序表移除该分类
            _categoryOrder.Remove(SanitizeCategory(category));
            await SaveCategoryOrderAsync();

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
