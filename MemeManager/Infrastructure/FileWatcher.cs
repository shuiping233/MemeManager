namespace MemeManager.Infrastructure;

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
    private static readonly TimeSpan Debounce = AppConstants.FileWatchDebounce;

    public sealed record Change(string Category, string FileName);

    public sealed record Move(Change From, Change To);

    public event Action<IReadOnlyList<Change>>? FilesRemoved;

    public event Action<IReadOnlyList<Change>>? FilesAdded;

    public event Action<IReadOnlyList<Move>>? FilesMoved;

    private readonly string _baseDir;
    private readonly FileSystemWatcher _fsw;
    private readonly object _lock = new();

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

    public void Start()
    {
        _fsw.EnableRaisingEvents = true;
        Logger.Log("[FileWatcher] 已开始文件监听");
    }

    public void Stop()
    {
        _fsw.EnableRaisingEvents = false;
        _debounceTimer?.Change(Timeout.Infinite, 0);
        Logger.Log("[FileWatcher] 已停止文件监听");
    }

    private Change ToChange(string fullPath)
    {
        var rel = Path.GetRelativePath(_baseDir, fullPath);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var category = parts.Length > 1 ? parts[0] : string.Empty;
        return new Change(category, Path.GetFileName(fullPath));
    }

    private static bool ShouldTrack(string fullPath)
    {
        var ext = Path.GetExtension(fullPath);
        if (!AppConstants.IsImage(ext)) return false;

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
        if (Directory.Exists(e.FullPath)) return;
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

        lock (_lock)
        {
            if (ShouldTrack(e.OldFullPath)) _deleted.Add(e.OldFullPath);
            if (ShouldTrack(e.FullPath)) _created.Add(e.FullPath);
            if (_deleted.Count > 0 || _created.Count > 0)
                _debounceTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

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

        var deletedByFile = deleted.ToLookup(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
        var createdByFile = created.ToLookup(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

        var moves = new List<Move>();
        var removedChanges = new List<Change>();
        var addedChanges = new List<Change>();

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
            if (pairedFiles.Contains(g.Key)) continue;
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
