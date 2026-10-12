using System.Security.Cryptography;
using System.Text.Json;
using MemeManager.Models;
using MemeManager.Services;
using MemeManager.Views;

namespace MemeManager.Infrastructure;

/// <summary>
/// 一次批量导入的结果。
/// 除统计数字外，还带回“本次真正新建的 model 列表”（<see cref="Added"/>），
/// 让调用方能做“增量插入”而不是整表重建 —— 导入只增不减，重建整个网格
/// （Rebuild 策略下连图片都要重新解码）纯属浪费。
/// </summary>
public sealed record ImportResult(
    int Imported,
    int Duplicate,
    MemeModel? DuplicateModel,
    IReadOnlyList<MemeModel> Added)
{
    
    public static ImportResult Empty { get; } = new(0, 0, null, Array.Empty<MemeModel>());
}

public class MemeDataEngine(ConfigService _config)
{
    // 导入并行度：阶段1（算 hash+去重判定）与阶段2（File.Copy）各自的并发上限。
    // 兼顾 SSD 吞吐与句柄占用，后续调优直接改此处。
    private const int ImportParallelism = 16;

    
    private static JsonSerializerOptions JsonOptions => AppConstants.JsonOptions;

    private string _baseDir = AppConstants.DefaultMemeDataStoragePath();

    // 写/导入忙标志：保证同一时刻只有一个导入写任务在进行（数据安全）。
    // MainPage 路径靠自带的 ImageBatchOperationRunner 锁；Mini 拖入没有 runner，
    // 故走 ImportMemesSafeAsync 由本标志兜底（目前仅 ImageDragHelper 一处使用）。
    // ⚠️ 两把锁互不知情：本标志与 runner 的 _writeActive 并不互斥。
    private int _writeBusy;
    public bool IsBusyWriting => _writeBusy != 0;

    private readonly List<MemeModel> _memeCache = new();

    
    
    public FileWatcher? Watcher { get; private set; }


    
    private readonly Dictionary<string, List<string>> _titleReverseMap = new(StringComparer.OrdinalIgnoreCase);

    
    private readonly Dictionary<string, uint> _categoryOrder = new(StringComparer.OrdinalIgnoreCase);

    public string BaseDir => _baseDir;

    private AppConfig Config { get => _config.Config; set => _config.Config = value; }

    
    
    private static string ResolveBaseDir(string? storagePath)
    {
        string? candidate = string.IsNullOrWhiteSpace(storagePath) ? null : storagePath.Trim();
        if (!string.IsNullOrWhiteSpace(candidate) && !Path.IsPathRooted(candidate))
            candidate = null; 

        return string.IsNullOrWhiteSpace(candidate) ? AppConstants.DefaultMemeDataStoragePath() : candidate;
    }

    
    
    public string? LastDefaultCategoryWriteError { get; private set; }

    
    public string? LastBaseDirRevertError { get; private set; }

    

    public async Task InitializeAsync()
    {
        LoadConfig();

        if (string.IsNullOrWhiteSpace(Config.StoragePath))
            Config.StoragePath = _baseDir;

        _baseDir = ResolveBaseDir(Config.StoragePath);

        // 若配置里填了具体路径（非空），但该路径不存在或不是文件夹，认为配置损坏/失效，
        // 回退到默认数据目录并提示用户（保证程序正常启动，数据不丢在奇怪的地方）。
        if (!string.IsNullOrWhiteSpace(Config.StoragePath)
            && !Directory.Exists(Config.StoragePath))
        {
            string bad = Config.StoragePath;
            _baseDir = AppConstants.DefaultMemeDataStoragePath();
            Config.StoragePath = _baseDir;
            LastBaseDirRevertError = bad;
            Logger.Log($"[Engine] 配置的数据目录无效(不存在或非文件夹)，已回退默认: {bad} -> {_baseDir}");
        }
        
        else if (!string.Equals(_baseDir, Config.StoragePath, StringComparison.OrdinalIgnoreCase))
            Config.StoragePath = _baseDir;

        Directory.CreateDirectory(_baseDir);

        await LoadCategoryOrderAsync();
        await LoadAllMetadataAsync();

        // 初始化完成、目录就绪后再启动文件监听，避免启动期事件风暴。
        // F5 刷新等场景会反复调用 InitializeAsync：旧监听器先 Stop（释放 native 句柄、
        // 停止事件投递），再换新实例，避免 FileSystemWatcher 实例与句柄累积泄漏。
        Watcher?.Stop();
        Watcher = new FileWatcher(_baseDir);
        Watcher.Start();
    }

    
    private string CategoryOrderPath => Path.Combine(_baseDir, AppConstants.MetadataFileName);

    
    
    private void LoadConfig() => _config.LoadConfig();

    private async Task SaveConfigAsync() => await _config.SaveConfigAsync();

    
    
    public async Task UpdateConfigAsync(Action<AppConfig> patch)
    {
        await _config.UpdateConfigAsync(patch);

        string newBase = ResolveBaseDir(_config.Config.StoragePath);
        bool changed = !newBase.Equals(_baseDir, StringComparison.OrdinalIgnoreCase);
        _baseDir = newBase;

        
        
        if (changed)
            await LoadAllMetadataAsync();
    }

    

    private async Task LoadAllMetadataAsync()
    {
        
        await EcoQos.RunAsync(() =>
        {
            LoadAllMetadataCore();
        });
    }

    private void LoadAllMetadataCore()
    {
        _memeCache.Clear();
        _titleReverseMap.Clear();

        var dirs = Directory.GetDirectories(_baseDir);
        foreach (var dir in dirs)
        {
            var category = Path.GetFileName(dir);
            var metaPath = Path.Combine(dir, AppConstants.MetadataFileName);
            CategoryMetadata meta;
            if (File.Exists(metaPath))
            {
                try
                {
                    var json = File.ReadAllText(metaPath);
                    meta = JsonSerializer.Deserialize<CategoryMetadata>(json, JsonOptions)
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
                
                
                
                if (!IsSafeMetadataFileName(fileName))
                {
                    Logger.Log($"[Engine] 跳过非法 metadata 条目: key='{fileName}' 分类='{category}'");
                    continue;
                }

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

    

    public IReadOnlyList<MemeModel> GetAllMemes() => _memeCache.ToList();

    public IReadOnlyList<MemeModel> GetMemes(string? category = null, string? keyword = null)
    {
        // 快照后再做延迟 LINQ 枚举，避免枚举过程中 _memeCache 被其它线程（导入/删除/文件监听）
        // 并发修改导致 “Collection was modified” 崩溃。
        IEnumerable<MemeModel> query = _memeCache.ToList();

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(m => m.MatchesCategory(category));

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(m => m.HasKeyword(keyword));
        }

        // “全部表情”视图（category 为空）：跨分类合并，需按“分类顺序 + 分类内优先级”复合排序，
        // 否则各分类的 Priority 各自 1..N 会交错成乱序。合成键 = 分类顺序(高位 <<32) | 分类内优先级(低位)，
        // 两者均越大越靠前；低位用 0xFFFFFFFF 掩码防 uint 符号扩展。
        if (string.IsNullOrWhiteSpace(category))
        {
            return query
                .OrderByDescending(m =>
                    (_categoryOrder.TryGetValue(m.Category, out var catPrio) ? (long)catPrio : 0L) << 32
                    | (m.Priority & 0xFFFFFFFFL))
                .ThenByDescending(m => m.DateAdded)
                .ToList();
        }

        
        return query.OrderByDescending(m => m.Priority).ThenByDescending(m => m.DateAdded).ToList();
    }

    public IReadOnlyList<string> GetCategories()
    {
        
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var m in _memeCache.ToList())
            if (!string.IsNullOrWhiteSpace(m.Category)) set.Add(m.Category);

        if (!Directory.Exists(_baseDir)) return set.ToArray();
        foreach (var dir in Directory.GetDirectories(_baseDir))
        {
            
            if (File.Exists(Path.Combine(dir, AppConstants.MetadataFileName)))
                set.Add(Path.GetFileName(dir));
        }

        var result = set.ToArray();
        
        result.Sort((a, b) =>
        {
            int pa = _categoryOrder.TryGetValue(a, out var va) ? (int)va : 0;
            int pb = _categoryOrder.TryGetValue(b, out var vb) ? (int)vb : 0;
            int cmp = pb.CompareTo(pa);
            return cmp != 0 ? cmp : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    

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
                    var meta = JsonSerializer.Deserialize<CategoryOrderMetadata>(json, JsonOptions);
                    if (meta?.Categories != null)
                        foreach (var kv in meta.Categories)
                            _categoryOrder[kv.Key] = kv.Value.Priority;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Engine] 读取分类顺序失败: {ex.Message}");
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
            var json = JsonSerializer.Serialize(meta, JsonOptions);
            await File.WriteAllTextAsync(CategoryOrderPath, json);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Engine] 保存分类顺序失败: {ex.Message}");
        }
    }

    
    
    
    public async Task ReorderCategoriesAsync(IReadOnlyList<string> orderedNames)
    {
        uint p = (uint)orderedNames.Count;
        foreach (var name in orderedNames)
            _categoryOrder[name] = p--;
        await SaveCategoryOrderAsync();
    }

    
    public IReadOnlyList<string> ReverseLookupByTitle(string title)
    {
        if (_titleReverseMap.TryGetValue(title, out var list))
            return list;
        return new List<string>();
    }

    

    public async Task<(MemeModel? model, bool duplicate)> ImportMemeAsync(string sourcePath, string category, string? title = null, List<string>? tags = null)
    {
        if (!File.Exists(sourcePath)) return (null, false);
        
        
        if (!AppConstants.IsImage(Path.GetExtension(sourcePath)))
        {
            Logger.Log($"[Engine] 导入拒绝非图片扩展名: {sourcePath}");
            return (null, false);
        }

        try
        {
            string hash = await CalculateSha256Async(sourcePath);
            string ext = Path.GetExtension(sourcePath);
            string fileName = $"{hash}{ext}";

            
            
            
            
            var categoryDir = SafePath.CombineChildPath(_baseDir, SanitizeCategory(category));
            var targetPath = Path.Combine(categoryDir, fileName);
            var existing = _memeCache.FirstOrDefault(m =>
                m.Category.Equals(category, StringComparison.OrdinalIgnoreCase) && m.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!File.Exists(existing.LocalPath))
                {
                    Logger.Log($"[Engine] 缓存命中但磁盘文件已缺失(可能曾被移出): 文件={fileName} 分类={category}，清除僵尸缓存后重新导入");
                    _memeCache.Remove(existing);
                    if (!string.IsNullOrWhiteSpace(existing.Title) &&
                        _titleReverseMap.TryGetValue(existing.Title, out var rev))
                    {
                        rev.Remove(existing.FileName);
                        if (rev.Count == 0) _titleReverseMap.Remove(existing.Title);
                    }
                }
                else
                {
                    Logger.Log($"[Engine] 导入重复跳过: 文件={fileName} 源路径={sourcePath} 目标分类={category} (已存在于分类「{existing.Category}」)");
                    return (existing, true);
                }
            }

            Directory.CreateDirectory(categoryDir);
            await EcoQos.RunAsync(() => File.Copy(sourcePath, targetPath, overwrite: true));

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
            Logger.Log($"[Engine] 导入表情包失败: {ex.Message}");
            return (null, false);
        }
    }

    // 批量导入：与 ImportMemeAsync 单张逻辑一致，但目标分类的 .metadata.json
    // 只加载/保存一次（而非逐张各读写一次），并按分类预建文件名索引做 O(1) 去重，
    // 避免批量导入时大量冗余磁盘 IO 与 O(n2) 扫描。返回新增数、重复数及（仅当整体为单张
    // 且重复时的）重复模型，供调用方弹窗提示。
    public async Task<ImportResult> ImportMemesAsync(
        IEnumerable<string> sourcePaths, string category, IProgress<BatchProgress>? progress = null,
        Action<string>? onCategoryCreated = null)
    {
        var list = sourcePaths.ToList();
        uint total = (uint)list.Count;
        if (total == 0) return ImportResult.Empty;

        var safeTarget = SanitizeCategory(category);
        var categoryDir = SafePath.CombineChildPath(_baseDir, safeTarget);
        
        bool created = !Directory.Exists(categoryDir);
        Directory.CreateDirectory(categoryDir);
        if (created) onCategoryCreated?.Invoke(safeTarget);

        
        var meta = await LoadCategoryMetadataAsync(categoryDir);

        
        
        var existingByFile = _memeCache
            .Where(m => m.Category.Equals(safeTarget, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(m => m.FileName, m => m, StringComparer.OrdinalIgnoreCase);

        int imported = 0, duplicate = 0;
        MemeModel? duplicateModel = null;
        
        var added = new List<MemeModel>();

        
        var plans = new List<ImportPlan>(list.Count);

        
        var po = new ParallelOptions { MaxDegreeOfParallelism = ImportParallelism };
        await Parallel.ForEachAsync(list, po, async (sourcePath, _) =>
        {
            if (!File.Exists(sourcePath)) return;
            
            if (!AppConstants.IsImage(Path.GetExtension(sourcePath))) return;

            string hash;
            try { hash = await CalculateSha256Async(sourcePath); }
            catch (Exception ex)
            {
                Logger.Log($"[Engine] 导入算哈希失败: {sourcePath} {ex.Message}");
                return;
            }

            string ext = Path.GetExtension(sourcePath);
            string fileName = $"{hash}{ext}";

            
            bool isDuplicate = false;
            bool zombie = false;   
            if (existingByFile.TryGetValue(fileName, out var existing))
            {
                if (File.Exists(existing.LocalPath))
                    isDuplicate = true;
                else
                    zombie = true;
            }

            lock (plans)
                plans.Add(new ImportPlan(sourcePath, hash, ext, fileName, isDuplicate, zombie));
        });

        
        long ioDone = 0;

        
        var copyPo = new ParallelOptions { MaxDegreeOfParallelism = ImportParallelism };
        await Parallel.ForEachAsync(plans, copyPo, async (plan, _) =>
        {
            if (!plan.IsDuplicate)   
            {
                try
                {
                    await EcoQos.RunAsync(() =>
                        File.Copy(plan.SourcePath, Path.Combine(categoryDir, plan.FileName), overwrite: true));
                    plan.CopyOk = true;
                }
                catch (Exception ex)
                {
                    
                    Logger.Log($"[Engine] 导入复制失败: {plan.SourcePath} {ex.Message}");
                    plan.CopyOk = false;
                }
            }
            
            var d = (uint)Interlocked.Increment(ref ioDone);
            progress?.Report(new BatchProgress(d, total));
        });

        
        foreach (var plan in plans)
        {
            if (plan.IsDuplicate)
            {
                duplicate++;
                if (total == 1) duplicateModel = existingByFile.TryGetValue(plan.FileName, out var e) ? e : null;
                continue;
            }

            if (!plan.CopyOk)
            {
                
                continue;
            }

            
            if (plan.Zombie && existingByFile.TryGetValue(plan.FileName, out var dead))
            {
                _memeCache.Remove(dead);
                if (!string.IsNullOrWhiteSpace(dead.Title) &&
                    _titleReverseMap.TryGetValue(dead.Title, out var rev))
                {
                    rev.Remove(dead.FileName);
                    if (rev.Count == 0) _titleReverseMap.Remove(dead.Title);
                }
                existingByFile.Remove(plan.FileName);
            }

            uint maxPriority = 0;
            foreach (var entry in meta.Items.Values)
                if (entry.Priority > maxPriority) maxPriority = entry.Priority;

            var model = new MemeModel
            {
                Hash = plan.Hash,
                Extension = plan.Ext,
                LocalPath = Path.Combine(categoryDir, plan.FileName),
                Category = Path.GetFileName(categoryDir),
                Title = Path.GetFileNameWithoutExtension(plan.SourcePath),
                Tags = new List<string>(),
                DateAdded = DateTime.UtcNow,
                UsageCount = 0,
                Priority = maxPriority + 1
            };

            meta.Items[plan.FileName] = new MemeMetaEntry
            {
                Title = model.Title,
                Tags = model.Tags,
                Priority = model.Priority
            };

            _memeCache.Add(model);
            IndexTitle(model);
            existingByFile[plan.FileName] = model;   
            imported++;
            added.Add(model);
        }

        
        await SaveCategoryMetadataAsync(categoryDir, meta);
        return new ImportResult(imported, duplicate, duplicateModel, added);
    }

    /// <summary>
    /// 带“写忙”守卫的导入入口：同一时刻仅允许一个导入写任务进行（数据安全）。
    /// 已在进行中时直接返回空结果表示被拒（UI 据此提示“导入进行中”并忽略本次拖入）。
    /// Mini 等无 ImageBatchOperationRunner 的入口统一走这里；MainPage 自带 runner 锁，不强制改用。
    /// </summary>
    public async Task<ImportResult> ImportMemesSafeAsync(
        IEnumerable<string> sourcePaths, string category, IProgress<BatchProgress>? progress = null,
        Action<string>? onCategoryCreated = null)
    {
        if (Interlocked.Exchange(ref _writeBusy, 1) != 0)
            return ImportResult.Empty; 
        try
        {
            return await ImportMemesAsync(sourcePaths, category, progress, onCategoryCreated);
        }
        finally
        {
            Interlocked.Exchange(ref _writeBusy, 0);
        }
    }

    
    private sealed record ImportPlan(
        string SourcePath,
        string Hash,
        string Ext,
        string FileName,
        bool IsDuplicate,
        bool Zombie)
    {
        public bool CopyOk;
    }

    

    public async Task ExportMemesAsync(IEnumerable<MemeModel> memes, string targetDir, IProgress<BatchProgress>? progress = null)
    {
        Directory.CreateDirectory(targetDir);
        var list = memes.ToList();
        uint total = (uint)list.Count;
        
        long ioDone = 0;

        
        var copyPo = new ParallelOptions { MaxDegreeOfParallelism = ImportParallelism };
        await Parallel.ForEachAsync(list, copyPo, async (meme, _) =>
        {
            try
            {
                if (File.Exists(meme.LocalPath))
                {
                    var dest = Path.Combine(targetDir, meme.FileName);
                    await EcoQos.RunAsync(() => File.Copy(meme.LocalPath, dest, overwrite: true));
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Engine] 导出复制失败: {meme.LocalPath} {ex.Message}");
            }
            var d = (uint)Interlocked.Increment(ref ioDone);
            progress?.Report(new BatchProgress(d, total));
        });
    }

    // ---------- 移动到其他分类 ----------

    /// <summary>
    /// 检测移动冲突：若待移动的任意表情（排除本就在目标分类的项）其 hash 已存在于
    /// 目标分类，则返回该分类名；否则返回 null。用于在真正移动前提示用户，避免
    /// 同名(hash)文件被静默覆盖导致目标分类原有图片丢失。
    /// </summary>
    public async Task<string?> FindMoveConflictAsync(IEnumerable<MemeModel> memes, string targetCategory)
    {
        var safeTarget = SanitizeCategory(targetCategory);
        try
        {
            
            var existingHashes = GetMemes(safeTarget)
                .Select(m => m.Hash)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var m in memes)
            {
                
                if (m.Category.Equals(safeTarget, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (existingHashes.Contains(m.Hash))
                    return safeTarget;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Engine] 检测移动冲突失败: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 将一批表情移动到目标分类：移动物理文件、更新两分类的 metadata 与内存缓存。
    /// 若目标分类不存在会自动创建。
    /// </summary>
    public async Task MoveMemesToCategoryAsync(IEnumerable<MemeModel> memes, string targetCategory, IProgress<BatchProgress>? progress = null)
    {
        var safeTarget = SanitizeCategory(targetCategory);
        var targetDir = SafePath.CombineChildPath(_baseDir, safeTarget);
        var targetMeta = await LoadCategoryMetadataAsync(targetDir);
        bool targetDirty = false;

        
        
        uint targetMaxPriority = 0;
        foreach (var entry in targetMeta.Items.Values)
            if (entry.Priority > targetMaxPriority) targetMaxPriority = entry.Priority;

        var list = memes.ToList();
        uint total = (uint)list.Count;
        
        long ioDone = 0;

        
        
        
        var sourceDirs = list
            .Where(m => !m.Category.Equals(safeTarget, StringComparison.OrdinalIgnoreCase))
            .Select(m => SafePath.CombineChildPath(_baseDir, SanitizeCategory(m.Category)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sourceMetas = new Dictionary<string, (CategoryMetadata meta, bool dirty)>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in sourceDirs)
            sourceMetas[dir] = (await LoadCategoryMetadataAsync(dir), false);

        
        var plans = new List<MovePlan>(list.Count);

        
        var movePo = new ParallelOptions { MaxDegreeOfParallelism = ImportParallelism };
        await Parallel.ForEachAsync(list, movePo, async (meme, _) =>
        {
            try
            {
                
                if (meme.Category.Equals(safeTarget, StringComparison.OrdinalIgnoreCase))
                {
                    lock (plans) plans.Add(new MovePlan(meme, MoveResult.SkippedSameTarget));
                    return;
                }

                var destPath = Path.Combine(targetDir, meme.FileName);
                
                
                if (File.Exists(destPath))
                {
                    Logger.Log($"[Engine] 移动跳过(目标已存在): 文件={meme.FileName} 源分类=\"{meme.Category}\" -> 目标=\"{safeTarget}\"");
                    lock (plans) plans.Add(new MovePlan(meme, MoveResult.SkippedExists));
                    return;
                }

                try
                {
                    if (File.Exists(meme.LocalPath))
                        await EcoQos.RunAsync(() => File.Move(meme.LocalPath, destPath, overwrite: false));
                    lock (plans) plans.Add(new MovePlan(meme, MoveResult.Moved, destPath));
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Engine] 移动文件失败 {meme.FileName}: {ex.Message}");
                    lock (plans) plans.Add(new MovePlan(meme, MoveResult.Failed));
                }
            }
            finally
            {
                var d = (uint)Interlocked.Increment(ref ioDone);
                progress?.Report(new BatchProgress(d, total));
            }
        });

        
        foreach (var plan in plans)
        {
            var meme = plan.Meme;
            switch (plan.Result)
            {
                case MoveResult.SkippedSameTarget:
                case MoveResult.SkippedExists:
                case MoveResult.Failed:
                    continue;

                case MoveResult.Moved:
                    var sourceDir = SafePath.CombineChildPath(_baseDir, SanitizeCategory(meme.Category));
                    if (sourceMetas.TryGetValue(sourceDir, out var sm))
                    {
                        sm.meta.Items.Remove(meme.FileName);
                        sourceMetas[sourceDir] = (sm.meta, true);
                    }

                    
                    targetMeta.Items[meme.FileName] = new MemeMetaEntry
                    {
                        Title = meme.Title,
                        Tags = meme.Tags,
                        Priority = ++targetMaxPriority
                    };
                    targetDirty = true;

                    
                    meme.Category = safeTarget;
                    meme.LocalPath = plan.DestPath!;
                    meme.Priority = targetMaxPriority;

                    break;
            }
        }

        
        
        if (targetDirty)
        {
            Directory.CreateDirectory(targetDir);
            await SaveCategoryMetadataAsync(targetDir, targetMeta);
        }
        foreach (var (dir, entry) in sourceMetas)
            if (entry.dirty)
                await SaveCategoryMetadataAsync(dir, entry.meta);
    }

    
    private sealed record MovePlan(
        MemeModel Meme,
        MoveResult Result,
        string? DestPath = null);

    private enum MoveResult
    {
        Moved,
        SkippedSameTarget,
        SkippedExists,
        Failed
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

        var oldDir = SafePath.CombineChildPath(_baseDir, safeOld);
        var newDir = SafePath.CombineChildPath(_baseDir, safeNew);
        if (!Directory.Exists(oldDir)) return false;
        if (Directory.Exists(newDir)) return false; 

        try
        {
            Directory.Move(oldDir, newDir);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Engine] 重命名分类文件夹失败 {oldName}->{newName}: {ex.Message}");
            return false;
        }

        
        foreach (var m in _memeCache)
        {
            if (m.Category.Equals(safeOld, StringComparison.OrdinalIgnoreCase))
            {
                m.Category = safeNew;
                m.LocalPath = Path.Combine(newDir, m.FileName);
            }
        }

        
        if (Config.LastCategory.Equals(safeOld, StringComparison.OrdinalIgnoreCase))
        {
            Config.LastCategory = safeNew;
            await SaveConfigAsync();
        }

        
        if (_categoryOrder.TryGetValue(safeOld, out var prio))
        {
            _categoryOrder.Remove(safeOld);
            _categoryOrder[safeNew] = prio;
            await SaveCategoryOrderAsync();
        }

        Logger.Log($"[Engine] 重命名分类: {oldName} -> {newName}");
        return true;
    }

    // ---------- 重排（拖拽调整顺序） ----------

    /// <summary>
    /// 按给定文件名顺序（已是目标顺序）整体重算该分类的 Priority 为 1,2,3...
    /// 并写回 metadata 与内存缓存。
    /// </summary>
    public async Task ReorderMemesAsync(string category, IReadOnlyList<string> orderedFileNames)
    {
        var dir = SafePath.CombineChildPath(_baseDir, SanitizeCategory(category));
        var meta = await LoadCategoryMetadataAsync(dir);

        
        
        uint p = (uint)orderedFileNames.Count;
        bool changed = false;
        foreach (var fileName in orderedFileNames)
        {
            if (meta.Items.TryGetValue(fileName, out var entry))
            {
                if (entry.Priority != p) { entry.Priority = p; changed = true; }
                p--;
            }
        }
        
        uint tail = 0;
        foreach (var kv in meta.Items)
            if (kv.Value.Priority == 0 && !orderedFileNames.Contains(kv.Key))
            {
                if (kv.Value.Priority != tail) { kv.Value.Priority = tail; changed = true; }
                tail++;
            }

        
        if (changed)
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

    

    public async Task RenameMemeAsync(MemeModel meme, string newTitle)
    {
        var title = (newTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Equals(meme.Title, StringComparison.OrdinalIgnoreCase))
            return;

        var dir = SafePath.CombineChildPath(_baseDir, SanitizeCategory(meme.Category));
        var meta = await LoadCategoryMetadataAsync(dir);
        if (meta.Items.TryGetValue(meme.FileName, out var entry))
            entry.Title = title;

        await SaveCategoryMetadataAsync(dir, meta);

        
        if (_titleReverseMap.TryGetValue(meme.Title, out var list))
        {
            list.Remove(meme.FileName);
            if (list.Count == 0) _titleReverseMap.Remove(meme.Title);
        }
        meme.Title = title;
        IndexTitle(meme);
    }

    

    public async Task DeleteMemesAsync(IEnumerable<MemeModel> memes, IProgress<BatchProgress>? progress = null)
    {
        var list = memes.ToList();
        uint total = (uint)list.Count;
        
        long ioDone = 0;
        var byCategory = list.GroupBy(m => m.Category, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byCategory)
        {
            var categoryDir = SafePath.CombineChildPath(_baseDir, SanitizeCategory(group.Key));
            var meta = await LoadCategoryMetadataAsync(categoryDir);
            var groupList = group.ToList();

            
            var delPo = new ParallelOptions { MaxDegreeOfParallelism = ImportParallelism };
            await Parallel.ForEachAsync(groupList, delPo, async (meme, _) =>
            {
                try { if (File.Exists(meme.LocalPath)) File.Delete(meme.LocalPath); }
                catch (Exception ex)
                {
                    Logger.Log($"[Engine] 删除文件失败: {meme.LocalPath} {ex.Message}");
                }
                var d = (uint)Interlocked.Increment(ref ioDone);
                progress?.Report(new BatchProgress(d, total));
            });

            
            foreach (var meme in groupList)
            {
                meta.Items.Remove(meme.FileName);
                _memeCache.Remove(meme);
                if (!string.IsNullOrWhiteSpace(meme.Title) &&
                    _titleReverseMap.TryGetValue(meme.Title, out var revList))
                {
                    revList.Remove(meme.FileName);
                    if (revList.Count == 0) _titleReverseMap.Remove(meme.Title);
                }
            }

            await SaveCategoryMetadataAsync(categoryDir, meta);
        }
    }

    
    public void RemoveMemesFromCache(IEnumerable<MemeModel> memes)
    {
        foreach (var meme in memes)
        {
            _memeCache.Remove(meme);
            if (!string.IsNullOrWhiteSpace(meme.Title) &&
                _titleReverseMap.TryGetValue(meme.Title, out var list))
            {
                list.Remove(meme.FileName);
                if (list.Count == 0) _titleReverseMap.Remove(meme.Title);
            }
        }
    }

    

    public async Task<bool> AddCategoryAsync(string category)
    {
        var trimmed = (category ?? string.Empty).Trim();
        
        
        
        if (!FileNameValidator.IsValidCategoryName(trimmed))
            return false;

        var dir = SafePath.CombineChildPath(_baseDir, trimmed);
        if (Directory.Exists(dir)) return false;
        try
        {
            Directory.CreateDirectory(dir);
            await SaveCategoryMetadataAsync(dir, new CategoryMetadata());
            
            
            _categoryOrder[trimmed] = 0;
            await SaveCategoryOrderAsync();
            Logger.Log($"[Engine] 创建分类: {trimmed}");
            return true;
        }
        catch (Exception ex)
        {
            
            
            LastDefaultCategoryWriteError = $"{dir}: {ex.GetType().Name}: {ex.Message}";
            Logger.Log($"[Engine] 创建分类失败({trimmed}): {LastDefaultCategoryWriteError}");
            return false;
        }
    }

    // 同步确保存在 Default 分类（供 UI 线程的 LoadCategories 调用，避免 async 死锁）
    public void EnsureDefaultCategory()
    {
        var dir = SafePath.CombineChildPath(_baseDir, SanitizeCategory(AppConstants.DefaultCategory));
        if (Directory.Exists(dir)) return;
        Directory.CreateDirectory(dir);
        try
        {
            var metaPath = Path.Combine(dir, AppConstants.MetadataFileName);
            if (!File.Exists(metaPath))
                File.WriteAllTextAsync(metaPath,
                    JsonSerializer.Serialize(new CategoryMetadata(), JsonOptions))
                    .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Log($"[Engine] 创建默认分类失败: {ex.Message}");
        }
    }

    

    public async Task<bool> DeleteCategoryAsync(string category)
    {
        var dir = SafePath.CombineChildPath(_baseDir, SanitizeCategory(category));
        if (!Directory.Exists(dir)) return false;

        try
        {
            
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

            
            Directory.Delete(dir, recursive: true);

            
            _categoryOrder.Remove(SanitizeCategory(category));
            await SaveCategoryOrderAsync();

            Logger.Log($"[Engine] 删除分类: {category}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Engine] 删除分类失败: {ex.Message}");
            return false;
        }
    }

    

    private async Task<CategoryMetadata> LoadCategoryMetadataAsync(string categoryDir)
    {
        var metaPath = Path.Combine(categoryDir, AppConstants.MetadataFileName);
        if (!File.Exists(metaPath)) return new CategoryMetadata();
        try
        {
            var json = await File.ReadAllTextAsync(metaPath);
            return JsonSerializer.Deserialize<CategoryMetadata>(json, JsonOptions)
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
        var metaPath = Path.Combine(categoryDir, AppConstants.MetadataFileName);
        var json = JsonSerializer.Serialize(meta, JsonOptions);
        await File.WriteAllTextAsync(metaPath, json);
    }

    

    
    
    private static bool IsSafeMetadataFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        
        
        if (!fileName.Equals(Path.GetFileName(fileName), StringComparison.Ordinal))
            return false;
        if (fileName is "." or "..")
            return false;
        
        return AppConstants.IsImage(Path.GetExtension(fileName));
    }

    private static string SanitizeCategory(string category)
    {
        
        
        
        var trimmed = (category ?? string.Empty).Trim();
        if (!FileNameValidator.IsValidCategoryName(trimmed))
            return AppConstants.UncategorizedCategory;
        return trimmed;
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
