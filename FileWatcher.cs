using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MemeManager;

// 监听数据目录的文件系统变化，负责"探测文件真相"：
// 拖拽/移动/删除/导入都会在极短时间内产生一串事件（同盘 Move 表现为
// 源 Deleted + 目标 Created，且文件名 hash+ext 不变只是目录——即分类——变了），
// 故用 500ms 防抖收集所有事件，按"全路径"识别移动（同文件名 + 不同分类目录 = 移动），
// 并区分出：真正消失(Removed) / 真正新增(Added) / 库内移动(Moved)。
//
// 本类只探测、不碰 UI/业务：防抖结束后把"带分类名的结果"通过事件抛给订阅方
// （MainWindow），由订阅方按当前焦点分类决定是否更新控件，从而与 UI 解耦。
public sealed class FileWatcher : IDisposable
{
    // 防抖静默时长：连续事件流停止后多久统一处理
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    // 单次变化的描述：归属分类 + 文件名(hash+ext)
    public sealed record Change(string Category, string FileName);

    // 移动：旧路径 -> 新路径（均相对/绝对可辨），带两侧的分类与文件名
    public sealed record Move(Change From, Change To);

    // 真正从库中消失的文件（含分类，供订阅方按焦点分类过滤）
    public event Action<IReadOnlyList<Change>>? FilesRemoved;

    // 真正新增的文件（手动往分类文件夹塞图等兜底场景）
    public event Action<IReadOnlyList<Change>>? FilesAdded;

    // 库内移动（如移动到其他分类）。该语义在软件内已由拖拽事件处理，
    // 故 MainWindow 暂不订阅此事件——此处仅保留分发能力，不主动触发订阅方逻辑。
    public event Action<IReadOnlyList<Move>>? FilesMoved;

    private readonly string _baseDir;
    private readonly FileSystemWatcher _fsw;
    private readonly object _lock = new();
    // 收集全路径（而非仅文件名），移动识别依赖分类目录差异
    private readonly List<string> _deleted = new();
    private readonly List<string> _created = new();
    private Timer? _debounceTimer;

    public FileWatcher(string baseDir)
    {
        _baseDir = baseDir;
        _fsw = new FileSystemWatcher(baseDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = false,
        };
        _fsw.Deleted += OnDeleted;
        _fsw.Renamed += OnRenamed;
        _debounceTimer = new Timer(_ => Flush());
    }

    // 引擎初始化完成、目录已就绪后再开启监听，避免启动期事件风暴干扰
    public void Start() => _fsw.EnableRaisingEvents = true;

    // 停止监听（窗口关闭时调用）：关闭事件投递与防抖计时器，但保留对象供下次 Start。
    public void Stop()
    {
        _fsw.EnableRaisingEvents = false;
        _debounceTimer?.Change(Timeout.Infinite, 0);
        Logger.Log("[FileWatcher] 已停止文件监听");
    }

    // 从全路径提取归属分类：_baseDir 下第一级子目录名即分类名；
    // 若文件直接在 _baseDir 根（理论上不会），分类记为空串。
    private Change ToChange(string fullPath)
    {
        var rel = Path.GetRelativePath(_baseDir, fullPath);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var category = parts.Length > 1 ? parts[0] : string.Empty;
        return new Change(category, Path.GetFileName(fullPath));
    }

    // 仅关注图片文件；log 目录与所有 *.json/*.log 等非图片一律忽略，避免噪声触发。
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tiff", ".tif", ".avif", ".heic", ".heif"
    };

    private static bool ShouldTrack(string fullPath)
    {
        var ext = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(ext) || !ImageExtensions.Contains(ext)) return false;
        // 忽略 log 目录（Logger 写到 <baseDir>/log/debug.log）
        var dir = Path.GetDirectoryName(fullPath);
        if (dir != null)
        {
            var leaf = new DirectoryInfo(dir).Name;
            if (string.Equals(leaf, "log", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (Directory.Exists(e.FullPath)) return; // 只关心文件
        if (!ShouldTrack(e.FullPath)) return;
        lock (_lock)
        {
            _deleted.Add(e.FullPath);
            _debounceTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (Directory.Exists(e.FullPath)) return;
        // Renamed 同时视为"旧路径消失 + 新路径出现"，与 Deleted+Created 两事件等价处理
        lock (_lock)
        {
            if (ShouldTrack(e.OldFullPath)) _deleted.Add(e.OldFullPath);
            if (ShouldTrack(e.FullPath)) _created.Add(e.FullPath);
            if (_deleted.Count > 0 || _created.Count > 0)
                _debounceTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    // 防抖到点：配对移动、算差集并分发（不保证线程，订阅方自行回到 UI 线程）
    private void Flush()
    {
        List<string> deleted, created;
        lock (_lock)
        {
            deleted = _deleted.ToList();
            created = _created.ToList();
            _deleted.Clear();
            _created.Clear();
        }
        if (deleted.Count == 0 && created.Count == 0) return;

        // 按文件名分组，识别"同文件名 + 不同分类目录" = 移动
        var deletedByFile = deleted.ToLookup(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
        var createdByFile = created.ToLookup(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

        var moves = new List<Move>();
        var removedChanges = new List<Change>();
        var addedChanges = new List<Change>();

        // 已配对的文件名集合，避免重复计入
        var pairedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in deletedByFile)
        {
            var createdSame = createdByFile[g.Key].FirstOrDefault();
            if (createdSame != null)
            {
                // 比较分类目录是否不同：不同 = 移动；相同 = 同目录改名(罕见)，归入删+增
                var from = ToChange(g.First());
                var to = ToChange(createdSame);
                if (!string.Equals(from.Category, to.Category, StringComparison.OrdinalIgnoreCase))
                {
                    moves.Add(new Move(from, to));
                    pairedFiles.Add(g.Key);
                    continue;
                }
            }
            removedChanges.Add(ToChange(g.First()));
        }

        foreach (var g in createdByFile)
        {
            if (pairedFiles.Contains(g.Key)) continue; // 已作为移动配对
            addedChanges.Add(ToChange(g.First()));
        }

        if (moves.Count > 0) FilesMoved?.Invoke(moves);
        if (removedChanges.Count > 0) FilesRemoved?.Invoke(removedChanges);
        if (addedChanges.Count > 0) FilesAdded?.Invoke(addedChanges);
    }

    public void Dispose()
    {
        _fsw.EnableRaisingEvents = false;
        _fsw.Deleted -= OnDeleted;
        _fsw.Renamed -= OnRenamed;
        _debounceTimer?.Dispose();
        _fsw.Dispose();
    }
}
