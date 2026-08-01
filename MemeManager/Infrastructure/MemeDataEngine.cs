using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MemeManager.Views;
using MemeManager.Models;

namespace MemeManager.Infrastructure;

public class MemeDataEngine
{
    // 默认分类名（UI 初次启动、无任何分类时创建）。统一在此定义，避免 "Default" 字面量散落。
    public const string DefaultCategory = "Default";

    // 默认数据目录名（位于“图片”库或 LocalApplicationData 下）。统一在此定义，避免 "MeMeManagerData" 字面量散落。
    public const string DefaultDataFolderName = "MeMeManagerData";

    // 导入并行度：阶段1（算 hash+去重判定）与阶段2（File.Copy）各自的并发上限。
    // 兼顾 SSD 吞吐与句柄占用，后续调优直接改此处。
    private const int ImportParallelism = 16;

    // 分类元数据文件名（每个分类目录下的 .metadata.json）
    private const string MetadataFileName = ".metadata.json";

    // 分类名为空/非法时的兜底分类名（走 i18n），公开供 UI 层在“全部表情”视图下
    // 将外部拖入的图片归入此分类（而非误用视图标记值）。
    public static string UncategorizedCategory => Localization.Get("Category_Uncategorized");

    // 写盘 JSON：缩进可读 + 中文不转义（便于人工查看/修改）
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 4,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private string _baseDir;

    // 写/导入忙标志：保证同一时刻只有一个导入写任务在进行（数据安全）。
    // MainPage 通过自带的 ImageBatchOperationRunner 已有锁；Mini 等没有 runner 的入口
    // 统一走 ImportMemesSafeAsync，由本标志兜底拒绝并发导入。也可供 UI 判断是否“导入中”。
    private int _writeBusy;
    public bool IsBusyWriting => _writeBusy != 0;

    private readonly List<MemeModel> _memeCache = new();

    // 数据目录文件监听：探测图片文件从库中消失（外部拖出/被删），
    // 通过事件把结果交给 UI 层处理（与 UI 解耦）。
    public FileWatcher? Watcher { get; private set; }


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
        // 优先用“图片”库；若其为空/未配置（某些精简系统或域环境会返回空串），
        // 回退到 LocalApplicationData，避免拼接出相对路径或应用自身目录。
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures) || !Path.IsPathRooted(pictures))
            pictures = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(pictures, DefaultDataFolderName);
    }

    // 解析实际数据目录：确保绝对且绝不落在应用自身目录内（防止把数据/分类写到 exe 目录下
    // 导致无写权限崩溃，如 D:\MemeManager\Default 被拒）。空/非法/等于应用目录时回退到默认路径。
    private static string ResolveBaseDir(string? storagePath)
    {
        string? candidate = string.IsNullOrWhiteSpace(storagePath) ? null : storagePath.Trim();
        if (!string.IsNullOrWhiteSpace(candidate) && !Path.IsPathRooted(candidate))
            candidate = null; // 拒绝相对路径，避免相对 exe 目录

        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var cand = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // 若候选路径落在应用目录内（含应用目录本身），回退默认，避免污染/无权限。
            if (cand.Equals(appDir, StringComparison.OrdinalIgnoreCase)
                || cand.StartsWith(appDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                candidate = null;
        }

        return string.IsNullOrWhiteSpace(candidate) ? DefaultStoragePath() : candidate;
    }

    // 判断给定路径是否落在应用自身目录内（含应用目录本身）。供上层在用户主动设置目录时
    // 提示“不能设为应用文件夹”。
    public static bool IsInsideAppDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            return false;
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cand = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return cand.Equals(appDir, StringComparison.OrdinalIgnoreCase)
            || cand.StartsWith(appDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // 最近一次创建默认分类（AddCategoryAsync）时的写失败详情；为空表示成功。
    // 供启动流程在窗口就绪后弹窗提示用户（保证程序仍能启动，方便去设置里改目录）。
    public string? LastDefaultCategoryWriteError { get; private set; }

    // ---------- 配置 ----------

    public async Task InitializeAsync()
    {
        LoadConfig();

        _baseDir = ResolveBaseDir(Config.StoragePath);
        // 若实际使用的目录与配置中记录的不一致（被回退），同步修正配置以免下次重复踩坑。
        if (!string.Equals(_baseDir, Config.StoragePath, StringComparison.OrdinalIgnoreCase))
            Config.StoragePath = _baseDir;
        Directory.CreateDirectory(_baseDir);

        await LoadCategoryOrderAsync();
        await LoadAllMetadataAsync();

        // 初始化完成、目录就绪后再启动文件监听，避免启动期事件风暴
        Watcher = new FileWatcher(_baseDir);
        Watcher.Start();
    }

    // 配置文件固定保存在 %LOCALAPPDATA% 下（与数据目录解耦），否则迁移数据目录后二次启动读不到配置
    private static string ConfigDir => MainWindow.AppDataDir;
    private string ConfigPath => MainWindow.ConfigPath;

    // 分类顺序文件位于“数据保存目录/.metadata.json”（与分类子文件夹内的 .metadata.json 不同层级）
    private string CategoryOrderPath => Path.Combine(_baseDir, MetadataFileName);

    private void LoadConfig()
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
            Logger.Log($"[Engine] 读取配置失败: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(Config.StoragePath))
            Config.StoragePath = DefaultStoragePath();
    }

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
            Logger.Log($"[Engine] 保存配置失败: {ex.Message}");
        }
    }

    public async Task UpdateConfigAsync(Action<AppConfig> patch)
    {
        patch(Config);

        string newBase = ResolveBaseDir(Config.StoragePath);
        // 若用户选的路径落在应用自身目录内或非法，回退默认并写回配置。
        if (!string.Equals(newBase, Config.StoragePath, StringComparison.OrdinalIgnoreCase))
            Config.StoragePath = newBase;
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
        // 在后台线程（带 EcoQoS 节流）执行目录扫描与元数据加载，避免阻塞 UI。
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
            var metaPath = Path.Combine(dir, MetadataFileName);
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
        // 快照后再做延迟 LINQ 枚举，避免枚举过程中 _memeCache 被其它线程（导入/删除/文件监听）
        // 并发修改导致 “Collection was modified” 崩溃。
        IEnumerable<MemeModel> query = _memeCache.ToList();

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
        // 先快照再枚举，避免枚举过程中 _memeCache 被并发修改导致崩溃
        foreach (var m in _memeCache.ToList())
            if (!string.IsNullOrWhiteSpace(m.Category)) set.Add(m.Category);

        if (Directory.Exists(_baseDir))
        {
            foreach (var dir in Directory.GetDirectories(_baseDir))
            {
                // 仅将含有 .metadata.json 的文件夹视为有效分类
                if (File.Exists(Path.Combine(dir, MetadataFileName)))
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

            // 去重：同分类下文件名已存在则视为重复。
            // 注意：缓存命中但磁盘文件已不存在（如曾被拖出到外部文件夹被移走）的，
            // 不当作重复——否则重新导入同一张图会被误判“已存在”。此时先清除该僵尸缓存
            // 记录，再按新导入流程覆盖写入，保证库与磁盘一致。
            var categoryDir = Path.Combine(_baseDir, SanitizeCategory(category));
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
    public async Task<(int imported, int duplicate, MemeModel? duplicateModel)> ImportMemesAsync(
        IEnumerable<string> sourcePaths, string category, IProgress<BatchProgress>? progress = null,
        Action<string>? onCategoryCreated = null)
    {
        var list = sourcePaths.ToList();
        uint total = (uint)list.Count;
        if (total == 0) return (0, 0, null);

        var safeTarget = SanitizeCategory(category);
        var categoryDir = Path.Combine(_baseDir, safeTarget);
        // 仅在分类目录原本不存在（即本次新建）时通知上层，便于 UI 即时刷新分类栏。
        bool created = !Directory.Exists(categoryDir);
        Directory.CreateDirectory(categoryDir);
        if (created) onCategoryCreated?.Invoke(safeTarget);

        // 目标分类 metadata 仅加载一次
        var meta = await LoadCategoryMetadataAsync(categoryDir);

        // 预建“文件名 -> 缓存项”索引（仅本分类），O(1) 去重，避免逐张线性扫描。
        // 并行阶段只读取它（判定重复），绝不写入；写入留到阶段3串行。
        var existingByFile = _memeCache
            .Where(m => m.Category.Equals(safeTarget, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(m => m.FileName, m => m, StringComparer.OrdinalIgnoreCase);

        int imported = 0, duplicate = 0;
        MemeModel? duplicateModel = null;

        // 每张的判定/落地结果容器（并行阶段填充，不触碰共享字典）。
        var plans = new List<ImportPlan>(list.Count);

        // ---------- 阶段1：并行算 hash + 去重判定（只读 existingByFile）----------
        var po = new ParallelOptions { MaxDegreeOfParallelism = ImportParallelism };
        await Parallel.ForEachAsync(list, po, async (sourcePath, _) =>
        {
            if (!File.Exists(sourcePath)) return;

            string hash;
            try { hash = await CalculateSha256Async(sourcePath); }
            catch (Exception ex)
            {
                Logger.Log($"[Engine] 导入算哈希失败: {sourcePath} {ex.Message}");
                return;
            }

            string ext = Path.GetExtension(sourcePath);
            string fileName = $"{hash}{ext}";

            // 查预建索引判定重复（只读，线程安全）
            bool isDuplicate = false;
            bool zombie = false;   // 缓存命中但磁盘文件缺失，需在阶段3清理后覆盖
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

        // 进度计数器：阶段1/阶段2 每完成一项（含重复/跳过/失败）计数一次，阶段3 不再报，避免重复计数。
        long ioDone = 0;

        // ---------- 阶段2：并行复制文件（IO 并行，不碰共享状态）----------
        var copyPo = new ParallelOptions { MaxDegreeOfParallelism = ImportParallelism };
        await Parallel.ForEachAsync(plans, copyPo, async (plan, _) =>
        {
            if (!plan.IsDuplicate)   // 重复项直接跳过，无须复制
            {
                try
                {
                    await EcoQos.RunAsync(() =>
                        File.Copy(plan.SourcePath, Path.Combine(categoryDir, plan.FileName), overwrite: true));
                    plan.CopyOk = true;
                }
                catch (Exception ex)
                {
                    // 失败静默打日志跳过（沿用现状，不弹窗）
                    Logger.Log($"[Engine] 导入复制失败: {plan.SourcePath} {ex.Message}");
                    plan.CopyOk = false;
                }
            }
            // 该项 IO 处理完毕（无论复制/重复/失败），进度 +1
            var d = (uint)Interlocked.Increment(ref ioDone);
            progress?.Report(new BatchProgress(d, total));
        });

        // ---------- 阶段3：串行落地（写 metadata/cache，纯内存极快）----------
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
                // 复制失败的项不写入 metadata/cache
                continue;
            }

            // 僵尸缓存：先清掉缺失文件的旧缓存（仅串行阶段操作共享字典）
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
            existingByFile[plan.FileName] = model;   // 同批次内后续重复也能识别
            imported++;
        }

        // 整批仅写回一次 metadata
        await SaveCategoryMetadataAsync(categoryDir, meta);
        return (imported, duplicate, duplicateModel);
    }

    /// <summary>
    /// 带“写忙”守卫的导入入口：同一时刻仅允许一个导入写任务进行（数据安全）。
    /// 已在进行中时直接返回 (0,0,null) 表示被拒（UI 据此提示“导入进行中”并忽略本次拖入）。
    /// Mini 等无 ImageBatchOperationRunner 的入口统一走这里；MainPage 自带 runner 锁，不强制改用。
    /// </summary>
    public async Task<(int imported, int duplicate, MemeModel? duplicateModel)> ImportMemesSafeAsync(
        IEnumerable<string> sourcePaths, string category, IProgress<BatchProgress>? progress = null,
        Action<string>? onCategoryCreated = null)
    {
        if (Interlocked.Exchange(ref _writeBusy, 1) != 0)
            return (0, 0, null); // 已有导入任务在跑，拒绝本次
        try
        {
            return await ImportMemesAsync(sourcePaths, category, progress, onCategoryCreated);
        }
        finally
        {
            Interlocked.Exchange(ref _writeBusy, 0);
        }
    }

    // 导入单张的判定/落地计划（并行阶段填充，阶段3串行消费）
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

    // ---------- 导出 ----------

    public async Task ExportMemesAsync(IEnumerable<MemeModel> memes, string targetDir, IProgress<BatchProgress>? progress = null)
    {
        Directory.CreateDirectory(targetDir);
        var list = memes.ToList();
        uint total = (uint)list.Count;
        // 进度计数器：阶段1 每完成一项（复制 IO 完毕）计数一次
        long ioDone = 0;

        // 并行复制文件（IO 并行，互不依赖，无共享状态；失败静默忽略）
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
            // 目标分类已有的所有 hash（无需按 Category 过滤：GetMemes(target) 返回项本就属于 target）
            var existingHashes = GetMemes(safeTarget)
                .Select(m => m.Hash)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var m in memes)
            {
                // 本就在目标分类的项移动给自己，不算冲突（MoveMemesToCategoryAsync 也会跳过）
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
        var targetDir = Path.Combine(_baseDir, safeTarget);
        Directory.CreateDirectory(targetDir);
        var targetMeta = await LoadCategoryMetadataAsync(targetDir);

        // 目标分类当前最大优先级；移入项依次 +1，使其排到目标分类最前
        // （Priority 越大越靠前，与导入/重排语义一致）。
        uint targetMaxPriority = 0;
        foreach (var entry in targetMeta.Items.Values)
            if (entry.Priority > targetMaxPriority) targetMaxPriority = entry.Priority;

        var list = memes.ToList();
        uint total = (uint)list.Count;
        // 进度计数器：阶段1 每完成一项（移动 IO 完毕，含跳过/失败）计数一次，阶段2 不再报，避免重复计数。
        long ioDone = 0;

        // 各源分类的 metadata 仅加载/保存一次（避免逐张读写 .metadata.json）。
        // Key = 源目录路径，Value = (metadata, 是否已被修改需写回)。
        // 先按去重的源目录统一异步加载一次，避免在循环内混用同步等待。
        var sourceDirs = list
            .Where(m => !m.Category.Equals(safeTarget, StringComparison.OrdinalIgnoreCase))
            .Select(m => Path.Combine(_baseDir, SanitizeCategory(m.Category)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sourceMetas = new Dictionary<string, (CategoryMetadata meta, bool dirty)>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in sourceDirs)
            sourceMetas[dir] = (await LoadCategoryMetadataAsync(dir), false);

        // 每张移动的判定/落地计划（并行阶段填充，阶段2串行消费，不触碰共享字典）
        var plans = new List<MovePlan>(list.Count);

        // ---------- 阶段1：并行移动物理文件（IO 并行，互不依赖）----------
        var movePo = new ParallelOptions { MaxDegreeOfParallelism = ImportParallelism };
        await Parallel.ForEachAsync(list, movePo, async (meme, _) =>
        {
            try
            {
                // 已在目标分类：标记跳过
                if (meme.Category.Equals(safeTarget, StringComparison.OrdinalIgnoreCase))
                {
                    lock (plans) plans.Add(new MovePlan(meme, MoveResult.SkippedSameTarget));
                    return;
                }

                var destPath = Path.Combine(targetDir, meme.FileName);
                // 目标已存在同名(hash)文件：跳过移动，不覆盖（同名=同内容，原文件保留，
                // 避免即使守卫被绕过也静默丢失目标分类原有图片）。
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

        // ---------- 阶段2：串行落地（写 metadata/cache，纯内存极快）----------
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
                    var sourceDir = Path.Combine(_baseDir, SanitizeCategory(meme.Category));
                    if (sourceMetas.TryGetValue(sourceDir, out var sm))
                    {
                        sm.meta.Items.Remove(meme.FileName);
                        sourceMetas[sourceDir] = (sm.meta, true);
                    }

                    // 目标分类 metadata（移入项置顶：优先级 = 当前最大 + 1）
                    targetMeta.Items[meme.FileName] = new MemeMetaEntry
                    {
                        Title = meme.Title,
                        Tags = meme.Tags,
                        Priority = ++targetMaxPriority
                    };

                    // 更新内存缓存
                    meme.Category = safeTarget;
                    meme.LocalPath = plan.DestPath!;
                    meme.Priority = targetMaxPriority;

                    break;
            }
        }

        // 仅各源分类与目标分类各写回一次 metadata（而非逐张写回）
        await SaveCategoryMetadataAsync(targetDir, targetMeta);
        foreach (var (dir, entry) in sourceMetas)
            if (entry.dirty)
                await SaveCategoryMetadataAsync(dir, entry.meta);
    }

    // 移动单张的判定/落地计划（并行阶段填充，阶段2串行消费）
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
            Logger.Log($"[Engine] 重命名分类文件夹失败 {oldName}->{newName}: {ex.Message}");
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

    public async Task DeleteMemesAsync(IEnumerable<MemeModel> memes, IProgress<BatchProgress>? progress = null)
    {
        var list = memes.ToList();
        uint total = (uint)list.Count;
        // 进度计数器：阶段1 每完成一项（删除 IO 完毕）计数一次，阶段2 不再报，避免重复计数。
        long ioDone = 0;
        var byCategory = list.GroupBy(m => m.Category, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byCategory)
        {
            var categoryDir = Path.Combine(_baseDir, SanitizeCategory(group.Key));
            var meta = await LoadCategoryMetadataAsync(categoryDir);
            var groupList = group.ToList();

            // 阶段1：并行删除物理文件（IO 并行，互不依赖；失败静默忽略）
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

            // 阶段2：串行更新内存缓存与 metadata（共享字典/集合必须串行，纯内存极快）
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

    // 仅从内存缓存移除（文件已消失、metadata 由监听刷新负责），供文件监听回调使用
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

    // ---------- 分类管理 ----------

    public async Task<bool> AddCategoryAsync(string category)
    {
        var dir = Path.Combine(_baseDir, SanitizeCategory(category));
        if (Directory.Exists(dir)) return false;
        try
        {
            Directory.CreateDirectory(dir);
            await SaveCategoryMetadataAsync(dir, new CategoryMetadata());
            // 新分类默认优先级 0（排在同优先级最后），并持久化顺序
            _categoryOrder[category] = 0;
            await SaveCategoryOrderAsync();
            Logger.Log($"[Engine] 创建分类: {category}");
            return true;
        }
        catch (Exception ex)
        {
            // 写入失败（无写权限等）不抛异常，保证调用方（尤其是启动流程）能继续启动；
            // 记录详情供上层弹窗提示用户去设置里改目录。
            LastDefaultCategoryWriteError = $"{dir}: {ex.GetType().Name}: {ex.Message}";
            Logger.Log($"[Engine] 创建分类失败({category}): {LastDefaultCategoryWriteError}");
            return false;
        }
    }

    // 同步确保存在 Default 分类（供 UI 线程的 LoadCategories 调用，避免 async 死锁）
    public void EnsureDefaultCategory()
    {
        var dir = Path.Combine(_baseDir, SanitizeCategory(DefaultCategory));
        if (Directory.Exists(dir)) return;
        Directory.CreateDirectory(dir);
        try
        {
            var metaPath = Path.Combine(dir, MetadataFileName);
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

            Logger.Log($"[Engine] 删除分类: {category}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Engine] 删除分类失败: {ex.Message}");
            return false;
        }
    }

    // ---------- metadata 读写 ----------

    private async Task<CategoryMetadata> LoadCategoryMetadataAsync(string categoryDir)
    {
        var metaPath = Path.Combine(categoryDir, MetadataFileName);
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
        var metaPath = Path.Combine(categoryDir, MetadataFileName);
        var json = JsonSerializer.Serialize(meta, JsonOptions);
        await File.WriteAllTextAsync(metaPath, json);
    }

    // ---------- 工具 ----------

    private static string SanitizeCategory(string category)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. category.Where(c => !invalid.Contains(c))]).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? UncategorizedCategory : cleaned;
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
