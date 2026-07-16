using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Threading;
using MemeManager.Data;
using MemeManager.Models;
using MemeManager.ViewModels;

namespace MemeManager;

public sealed partial class MainWindow : Window
{
    private IntPtr _hWnd;
    private bool _isVisible = true;
    private const int HOTKEY_ID = 9001;
    private const uint SUBCLASS_ID = 101;

    private const int MoveConflictLabelMaxLen = 32;

    private readonly NativeMethods.SUBCLASSPROC _subclassProc;
    private readonly NativeMethods.WinEventProc _winEventProc;
    private IntPtr _winEventHook;

    private readonly ObservableCollection<MemeViewModel> _memeList = new();
    private readonly ObservableCollection<CategoryViewModel> _categoryList = new();

    private IMemeListStrategy _listStrategy = new RebuildStrategy();

    private string _currentCategory = string.Empty;
    private bool _editMode;

    private string? _dragAnchorFileName;
    private bool _topMost = true;
    private List<MemeModel>? _draggingMemes;

    private IntPtr _prevActiveHwnd;
    private IntPtr _lastExternalFg;
    private bool _isActive;
    private DispatcherTimer? _fgTimer;

    private int _lastShiftAnchor = -1;
    private bool _pasteDialogOpen;

    public bool IsFilePickerOpen { get; internal set; }

    public void ApplyPreviewDelayFromConfig()
    {
        // 把配置里的悬停延迟应用到每个表情项的 ToolTip（覆盖默认 400ms）。
        // 注意：ToolTip.ShowDelayProperty.OverrideMetadata 在 Control 上不可靠，
        // 需逐个容器用 ToolTip.SetShowDelay 设置才能生效。
        try
        {
            var cfg = App.DataEngine.Config;
            int ms = cfg?.PreviewDelayMs > 0 ? cfg.PreviewDelayMs : 400;
            for (int i = 0; i < MemeGridView.Items.Count; i++)
            {
                if (MemeGridView.ContainerFromIndex(i) is Control container)
                    ToolTip.SetShowDelay(container, ms);
            }
        }
        catch { }
    }

    private bool _allowClose;
    private bool _isClosing;

    public MainWindow()
    {
        ((App)Application.Current!).RegisterMainWindow(this);

        InitializeComponent();

        ApplyListStrategyFromConfig();

        _hWnd = GetHwnd();

        ApplyPreviewDelayFromConfig();

        TopMostToggle.IsChecked = _topMost;

        SetTaskbarIcon();

        int exStyle = NativeMethods.GetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOPMOST);

        RegisterConfiguredHotKey();

        _subclassProc = NewWindowProc;
        NativeMethods.SetWindowSubclass(_hWnd, _subclassProc, SUBCLASS_ID, IntPtr.Zero);

        _winEventProc = new NativeMethods.WinEventProc(WinEventCallback);
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero,
            _winEventProc,
            NativeMethods.GetCurrentProcessId(),
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        NativeMethods.DragAcceptFiles(_hWnd, true);

        RestoreWindowSize();

        CategoryList.ItemsSource = _categoryList;
        MemeGridView.ItemsSource = _memeList;

        MemeGridView.AddHandler(DragDrop.DragOverEvent, MemeGridView_DragOver);
        MemeGridView.AddHandler(DragDrop.DropEvent, MemeGridView_Drop);
        CategoryList.ContainerPrepared += (_, args) =>
        {
            var c = args.Container;
            DragDrop.SetAllowDrop(c, true);
            c.AddHandler(DragDrop.DragOverEvent, CategoryListItem_DragOver);
            c.AddHandler(DragDrop.DropEvent, CategoryListItem_Drop);
        };

        Closed += Window_Closed;

        _fgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _fgTimer.Tick += (_, _) =>
        {
            if (!_isActive)
            {
                var fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != _hWnd)
                    _lastExternalFg = fg;
            }
        };

        this.KeyDown += Root_KeyDown;

        LoadCategories();
    }

    private IntPtr GetHwnd()
    {
        var handle = TryGetPlatformHandle();
        if (handle != null) return handle.Handle;
        return IntPtr.Zero;
    }

    // ---------- 窗口尺寸持久化 ----------
    private void RestoreWindowSize()
    {
        var cfg = App.DataEngine.Config;
        int w = (int)Math.Max(400, cfg.WindowWidth);
        int h = (int)Math.Max(300, cfg.WindowHeight);
        this.Width = w;
        this.Height = h;
        Log($"[窗口] 还原尺寸 {w}x{h} (预设={cfg.WindowSizePreset})");
    }

    private void SaveWindowSize()
    {
        var cfg = App.DataEngine.Config;
        bool maximized = this.WindowState == WindowState.Maximized;
        if (maximized)
        {
            Log("[窗口] 当前为最大化，跳过尺寸记录");
            return;
        }
        cfg.WindowWidth = double.IsNaN(this.Width) ? 950 : this.Width;
        cfg.WindowHeight = double.IsNaN(this.Height) ? 750 : this.Height;
        cfg.WindowMaximized = false;
        cfg.WindowSizePreset = ClassifySize(cfg.WindowWidth, cfg.WindowHeight);
        Log($"[窗口] 保存尺寸 {cfg.WindowWidth}x{cfg.WindowHeight} (预设={cfg.WindowSizePreset})");
        _ = App.DataEngine.SaveConfigAsync();
    }

    private static WindowSizePreset ClassifySize(double w, double h)
    {
        return (w, h) switch
        {
            (<= 800, <= 620) => WindowSizePreset.Small,
            (>= 1150, >= 880) => WindowSizePreset.Large,
            _ => WindowSizePreset.Medium
        };
    }

    public void ReloadData() => LoadCategories();

    private static IMemeListStrategy CreateStrategy(bool reuse) =>
        reuse ? new ReuseStrategy() : new RebuildStrategy();

    public void ApplyListStrategyFromConfig()
    {
        bool reuse = App.DataEngine.Config.UseControlReuse;
        var prev = _listStrategy;
        _listStrategy = CreateStrategy(reuse);
        if (prev != null)
            Log($"[策略] 列表策略切换为: {(reuse ? "复用(Reuse)" : "重建(Rebuild)")}");
        else
            Log($"[策略] 列表策略初始化为: {(reuse ? "复用(Reuse)" : "重建(Rebuild)")}");
    }

    private void LoadCategories()
    {
        if (App.DataEngine.GetCategories().Count == 0)
            App.DataEngine.EnsureDefaultCategory();

        _listStrategy.SyncCategories(
            _categoryList,
            App.DataEngine.GetCategories(),
            cat => App.DataEngine.GetMemes(cat).Count);

        var last = App.DataEngine.Config.LastCategory;
        var target = _categoryList.FirstOrDefault(c => c.Name == last) ?? _categoryList.FirstOrDefault();
        if (target != null && !target.Name.Equals(_currentCategory, StringComparison.OrdinalIgnoreCase))
        {
            CategoryList.SelectedItem = target;
            _currentCategory = target.Name;
        }
        else if (target != null)
        {
            CategoryList.SelectedItem = target;
        }

        RefreshMemes();
    }

    private void CategoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is CategoryViewModel cat)
        {
            if (cat.Name.Equals(_currentCategory, StringComparison.OrdinalIgnoreCase))
                return;
            _currentCategory = cat.Name;
            _ = App.DataEngine.UpdateConfigAsync(c => c.LastCategory = cat.Name);
            RefreshMemes();
        }
    }

    private CategoryViewModel? _contextCategory;

    private void CategoryItemContextFlyout_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is ContextMenu menu && menu.Parent is Control fe)
            _contextCategory = fe.DataContext as CategoryViewModel;
        if (_contextCategory != null)
            Log($"右键分类项: {_contextCategory.Name}");
    }

    private void CategoryOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_contextCategory == null) return;
        var dir = Path.Combine(App.DataEngine.BaseDir, _contextCategory.Name);
        Utils.OpenInExplorer(dir, select: false, logTag: "打开分类文件夹");
    }

    private async void CategoryNew_Click(object? sender, RoutedEventArgs e)
        => await ShowAddCategoryDialog();

    private async void CategoryDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (_contextCategory != null)
            await DeleteCategoryConfirmed(_contextCategory);
    }

    private async void CategoryRename_Click(object? sender, RoutedEventArgs e)
    {
        if (_contextCategory != null)
            await ShowRenameCategoryDialog(_contextCategory);
    }

    private async Task ShowRenameCategoryDialog(CategoryViewModel cat)
    {
        var input = await Dialogs.ShowInputAsync(this, "重命名分类", "输入新的分类名称", cat.Name);
        var newName = input?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName)) return;

        bool ok = await App.DataEngine.RenameCategoryAsync(cat.Name, newName);
        if (!ok)
        {
            await Dialogs.ShowMessageAsync(this, "重命名失败", "分类重命名失败（可能名称已存在或文件夹无法访问）。");
            return;
        }

        Log($"重命名分类「{cat.Name}」-> 「{newName}」");
        LoadCategories();
    }

    private async Task DeleteCategoryConfirmed(CategoryViewModel cat)
    {
        var confirm = await Dialogs.ShowConfirmAsync(this, "删除分类",
            $"确定要删除分类「{cat.Name}」吗？\n该分类下的所有表情与文件夹都会被删除。");
        if (!confirm) return;

        bool ok = await App.DataEngine.DeleteCategoryAsync(cat.Name);
        if (!ok) return;

        for (int i = _categoryList.Count - 1; i >= 0; i--)
            if (_categoryList[i].Name.Equals(cat.Name, StringComparison.OrdinalIgnoreCase))
                _categoryList.RemoveAt(i);

        if (_currentCategory.Equals(cat.Name, StringComparison.OrdinalIgnoreCase))
        {
            _currentCategory = _categoryList.FirstOrDefault()?.Name ?? string.Empty;
            CategoryList.SelectedItem = _categoryList.FirstOrDefault();
        }
        RefreshMemes();
    }

    private async void CategoryListItem_Drop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        var paths = (e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>())
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
        if (paths.Count == 0) return;

        var targetCat = (sender as Control)?.DataContext as CategoryViewModel;
        Log($"[分类Drop] 触发, 目标分类={targetCat?.Name ?? "(无)"}, 项数={paths.Count}");
        if (targetCat == null) return;

        var memes = paths.Select(BuildMemeFromPath).ToList();
        int moved = memes.Count(m => !m.Category.Equals(targetCat.Name, StringComparison.OrdinalIgnoreCase));
        if (moved > 0)
        {
            if (!await GuardMoveConflictAsync(memes, targetCat.Name))
                return;
            await App.DataEngine.MoveMemesToCategoryAsync(memes, targetCat.Name);
            Log($"Drop: 内部移动 {moved} 张图片到分类「{targetCat.Name}」");
            RemoveFromCurrentView(memes);
            UpdateCategoryCounts();
        }
    }

    private void SetMemeViewVisible(bool visible)
    {
        if (visible)
        {
            if (MemeGridView.ItemsSource != _memeList)
            {
                CategoryList.ItemsSource = _categoryList;
                MemeGridView.ItemsSource = _memeList;
            }
            var sel = _categoryList.FirstOrDefault(c => c.Name == _currentCategory)
                      ?? _categoryList.FirstOrDefault();
            if (sel != null)
            {
                CategoryList.SelectedItem = null;
                Dispatcher.UIThread.Post(() => { CategoryList.SelectedItem = sel; });
            }
            _fgTimer?.Start();
        }
        else
        {
            _fgTimer?.Stop();
            MemeGridView.ItemsSource = null;
            CategoryList.ItemsSource = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (App.DataEngine.Config.UseControlReuse)
            {
                Log($"[内存诊断] 隐藏释放(复用模式): _memeList={_memeList.Count} _categoryList={_categoryList.Count} " +
                    $"VM存活Bitmap={MemeViewModel.LiveBitmapImageCount} " +
                    $"托管堆={GC.GetTotalMemory(false) / 1024}KB GC代数0/1/2={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
            }
        }
    }

    private async Task<List<MemeModel>> MemesFromFilesAsync(IEnumerable<string> paths)
    {
        var result = new List<MemeModel>();
        var all = App.DataEngine.GetAllMemes();
        foreach (var p in paths)
        {
            var name = Path.GetFileName(p);
            var m = all.FirstOrDefault(x => x.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (m != null) result.Add(m);
        }
        return result;
    }

    private void CategoryListItem_DragOver(object? sender, DragEventArgs e)
    {
        if (_draggingMemes != null && _draggingMemes.Count > 0)
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private async void AddCategoryButton_Click(object? sender, RoutedEventArgs e)
        => await ShowAddCategoryDialog();

    private async Task ShowAddCategoryDialog()
    {
        var box = await Dialogs.ShowInputAsync(this, "新增分类", "输入新分类名称");
        var name = box?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return;
        bool added = await App.DataEngine.AddCategoryAsync(name);
        if (added)
        {
            _categoryList.Add(new CategoryViewModel(name, 0));
            CategoryList.SelectedItem = _categoryList.Last();
        }
    }

    private void RefreshMemes()
    {
        var keyword = SearchBox.Text?.Trim();
        var memes = App.DataEngine.GetMemes(
            string.IsNullOrWhiteSpace(_currentCategory) ? null : _currentCategory,
            string.IsNullOrWhiteSpace(keyword) ? null : keyword);

        _listStrategy.RefreshMemes(_memeList, memes);

        if (_listStrategy is ReuseStrategy)
        {
            int newCount = memes.Count;
            int oldCount = _memeList.Count;
            Log($"[诊断] RefreshMemes VM数={_memeList.Count} 新项数={newCount}");
        }

        UpdateCategoryCounts();

        if (_editMode)
        {
            SetSelectionBoxVisible(true);
            SyncSelectionToViewModels();
        }

        // 每次重建列表项后，把悬停预览延迟应用到各 ToolTip 容器。
        ApplyPreviewDelayFromConfig();
    }

    private void InsertMemeAtFront(MemeViewModel vm) => _memeList.Insert(0, vm);

    private void RemoveFromCurrentView(IEnumerable<MemeModel> removed)
    {
        var names = new HashSet<string>(removed.Select(m => m.FileName), StringComparer.OrdinalIgnoreCase);
        for (int i = _memeList.Count - 1; i >= 0; i--)
            if (names.Contains(_memeList[i].FileName))
                _memeList.RemoveAt(i);
    }

    private void UpdateCategoryCounts()
    {
        var cache = App.DataEngine.GetAllMemes();
        foreach (var c in _categoryList)
            c.Count = cache.Count(m => m.Category.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateBatchButtons()
    {
        if (MemeGridView == null) return;
        bool anySelected = MemeGridView.SelectedItems!.Count > 0;
        if (BatchExportButton != null) BatchExportButton.IsEnabled = anySelected;
        if (BatchMoveButton != null) BatchMoveButton.IsEnabled = anySelected;
        if (DeleteButton != null) DeleteButton.IsEnabled = anySelected;
    }

    private void EditButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_editMode)
        {
            Log("退出多选模式(点击修改/完成按钮)");
            ExitEditMode();
        }
        else
        {
            Log("进入多选模式");
            _editMode = true;
            EditButton.Content = "完成";
            BatchBar.IsVisible = true;
            MemeGridView.SelectionMode = App.DataEngine.Config.ExplorerStyleMultiSelect
                ? SelectionMode.Multiple : (SelectionMode.Multiple | SelectionMode.Toggle);
            SetSelectionBoxVisible(App.DataEngine.Config.ExplorerStyleMultiSelect);
        }
    }

    private async void MemeItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control fe || fe.DataContext is not MemeViewModel clicked)
        {
            Log("MemeItem_Pressed: 取不到 MemeViewModel, sender=" + sender?.GetType().Name);
            return;
        }

        // 拖拽发起：在 meme 项上按下并移动时拖出/内部移动
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (_draggingMemes == null)
            {
                var selected = (MemeGridView.SelectedItems?.Cast<MemeViewModel>() ?? []).ToList();
                List<MemeViewModel> group;
                if (_editMode && selected.Count > 0 && selected.Contains(clicked))
                    group = selected;
                else
                    group = new List<MemeViewModel> { clicked };

                _draggingMemes = group.Select(m => m.Model).ToList();
                _dragAnchorFileName = clicked.FileName;
                Log($"DragStart: 拖出 {_draggingMemes.Count} 张图片 (首项 {group[0].Title}, 锚点={_dragAnchorFileName})");

                var data = new DataTransfer();
                foreach (var lp in group.Select(m => m.LocalPath))
                {
                    var f = await StorageProvider.TryGetFileFromPathAsync(lp);
                    if (f != null) data.Add(DataTransferItem.CreateFile(f));
                }

                var result = await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy | DragDropEffects.Move);
                // 拖拽结束
                if (_editMode && group.Count > 0)
                {
                    var orderedFileNames = _listStrategy.ComputeDragOrder(_memeList, _draggingMemes, _dragAnchorFileName)
                        ?? _memeList.Select(m => m.FileName).ToList();
                    try { await App.DataEngine.ReorderMemesAsync(_currentCategory, orderedFileNames); } catch (Exception ex) { Log($"[拖拽] ReorderMemesAsync 写回失败: {ex}"); }
                }
                _draggingMemes = null;
                _dragAnchorFileName = null;
            }
            return;
        }

        // 普通点击（非拖拽，左键抬起判定）
        int index = _memeList.IndexOf(clicked);
        if (_editMode)
        {
            _lastShiftAnchor = index;
            return;
        }

        IntPtr liveFg = NativeMethods.GetForegroundWindow();
        IntPtr target = IntPtr.Zero;
        if (_lastExternalFg != IntPtr.Zero && _lastExternalFg != _hWnd)
            target = _lastExternalFg;
        else if (_prevActiveHwnd != IntPtr.Zero && _prevActiveHwnd != _hWnd)
            target = _prevActiveHwnd;
        else if (liveFg != IntPtr.Zero && liveFg != _hWnd)
            target = liveFg;

        if (target == IntPtr.Zero || target == _hWnd)
        {
            Log($"单击(发送模式): 未解析到有效外部窗口(target={target})，取消本次粘贴");
            return;
        }

        Log($"单击(发送模式): 发送图片 {clicked.Title} 到前台窗口 target={target}");
        await PasteService.OutputMemeToCursorAsync(clicked.LocalPath, target);
        await App.DataEngine.IncrementUsageAsync(clicked.Hash);
    }

    private void MemeGridView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SyncSelectionToViewModels();
        UpdateBatchButtons();
    }

    private void SyncSelectionToViewModels()
    {
        if (_isClosing) return;
        var selected = new HashSet<MemeViewModel>(MemeGridView.SelectedItems?.Cast<MemeViewModel>() ?? []);
        foreach (var vm in _memeList)
            vm.IsSelected = selected.Contains(vm);
    }

    private static void Log(string msg) => Logger.Log($"[MemeManager] {msg}");

    private static MemeModel BuildMemeFromPath(string p)
    {
        var existing = App.DataEngine.GetAllMemes()
            .FirstOrDefault(m => string.Equals(m.LocalPath, p, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var name = Path.GetFileName(p);
        return new MemeModel
        {
            LocalPath = p,
            Hash = Path.GetFileNameWithoutExtension(name),
            Extension = Path.GetExtension(name)
        };
    }

    private void MemeGridView_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy | DragDropEffects.Link;
        e.Handled = true;
    }

    private async void MemeGridView_Drop(object? sender, DragEventArgs e)
    {
        Log("Drop 事件触发");
        var memes = new List<MemeModel>();
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            var paths = (e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>())
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToList();
            foreach (var p in paths)
            {
                if (IsImage(Path.GetExtension(p)))
                {
                    var existing = App.DataEngine.GetAllMemes()
                        .FirstOrDefault(m => m.FileName.Equals(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase));
                    if (existing != null) memes.Add(existing);
                    else memes.Add(BuildMemeFromPath(p));
                }
            }
        }

        int importedCount = 0;
        if (memes.Count > 0)
        {
            foreach (var m in memes)
            {
                if (File.Exists(m.LocalPath))
                {
                    var (imported, _) = await App.DataEngine.ImportMemeAsync(m.LocalPath, _currentCategory);
                    if (imported != null) importedCount++;
                }
            }
        }
        else
        {
            Log("拖入: 未导入任何图片（已忽略不符合要求的拖拽对象）");
        }

        if (importedCount > 0)
        {
            if (!_isClosing && _isVisible)
            {
                RefreshMemes();
                UpdateCategoryCounts();
            }
            Log($"拖入完成(GridView): 新增 {importedCount} 个图片");
        }
    }

    private void HandleDropFiles(IntPtr hDrop)
    {
        try
        {
            uint count = NativeMethods.DragQueryFile(hDrop, 0xFFFFFFFFu, IntPtr.Zero, 0);
            var paths = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                uint len = NativeMethods.DragQueryFile(hDrop, i, IntPtr.Zero, 0);
                if (len == 0) continue;
                IntPtr buf = Marshal.AllocCoTaskMem((int)(len + 1) * 2);
                try
                {
                    NativeMethods.DragQueryFile(hDrop, i, buf, len + 1);
                    string path = Marshal.PtrToStringUni(buf) ?? string.Empty;
                    Log($"拖入文件[{i}] = {path}");
                    if (File.Exists(path) && IsImage(Path.GetExtension(path)))
                        paths.Add(path);
                }
                finally { Marshal.FreeCoTaskMem(buf); }
            }
            NativeMethods.DragFinish(hDrop);

            if (paths.Count > 0)
                Log($"拖入 {paths.Count} 个文件, 目标分类={_currentCategory}");

            _ = ImportDroppedFilesAsync(paths);
        }
        catch (Exception ex)
        {
            Log("WM_DROPFILES 处理失败: " + ex.Message);
        }
    }

    private async Task ImportDroppedFilesAsync(List<string> paths)
    {
        if (paths.Count == 0) return;
        int importedCount = 0;
        int duplicateCount = 0;
        foreach (var path in paths)
        {
            var (imported, dup) = await App.DataEngine.ImportMemeAsync(path, _currentCategory);
            if (imported == null) continue;
            if (dup) duplicateCount++;
            else importedCount++;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing || !_isVisible)
            {
                Log($"[防护] 拖入刷新被守卫拦截(_isClosing={_isClosing}, _isVisible={_isVisible})，丢弃 {importedCount} 个导入");
                return;
            }
            RefreshMemes();
            UpdateCategoryCounts();
            Log($"拖入完成: 新增 {importedCount} 个, 重复跳过 {duplicateCount} 个");
        });
    }

    private MemeViewModel? _contextMeme;

    private void MemeItemContextFlyout_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu flyout || flyout.Parent is not Control fe)
            return;
        if (fe.DataContext is not MemeViewModel vm)
            return;

        _contextMeme = vm;
        Log("右键单击表情项: " + vm.Title);

        // 重建“移动到其他分类”子菜单
        if (flyout.Items.FirstOrDefault() is MenuItem moveSub && moveSub.Header?.ToString() == "移动到其他分类")
        {
            moveSub.Items.Clear();
            bool hasTarget = false;
            foreach (var cat in _categoryList)
            {
                if (cat.Name.Equals(vm.Category, StringComparison.OrdinalIgnoreCase)) continue;
                hasTarget = true;
                var targetName = cat.Name;
                var moveItem = new MenuItem { Header = cat.Name };
                moveItem.Click += (_, __) => MoveMemeToCategory(vm, targetName);
                moveSub.Items.Add(moveItem);
            }
            moveSub.IsEnabled = hasTarget;
        }
    }

    private async void MemeDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (_contextMeme == null) return;
        var vm = _contextMeme;
        var confirm = await Dialogs.ShowConfirmAsync(this, "删除确认", $"确定要删除「{vm.Title}」吗？");
        if (confirm)
        {
            await App.DataEngine.DeleteMemesAsync(new[] { vm.Model });
            var item = _memeList.FirstOrDefault(m => m == vm);
            if (item != null) _memeList.Remove(item);
            UpdateCategoryCounts();
        }
    }

    private void MemeOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (_contextMeme == null) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _contextMeme.Model.LocalPath, UseShellExecute = true });
        }
        catch (Exception ex) { Log($"[打开图片] 失败: {ex.Message}"); }
    }

    private void MemeOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_contextMeme == null) return;
        Utils.OpenInExplorer(_contextMeme.Model.LocalPath, select: true, logTag: "打开所在文件夹");
    }

    private async void MemeRename_Click(object? sender, RoutedEventArgs e)
    {
        if (_contextMeme == null) return;
        var vm = _contextMeme;
        var input = await Dialogs.ShowInputAsync(this, "重命名", "输入新的名称", vm.Title);
        if (input == null) return;
        await App.DataEngine.RenameMemeAsync(vm.Model, input);
        Log($"重命名「{vm.Title}」-> 「{input}」");
        vm.Title = input;
    }

    private async Task<bool> GuardMoveConflictAsync(IEnumerable<MemeModel> memes, string targetCategory)
    {
        var conflict = await App.DataEngine.FindMoveConflictAsync(memes, targetCategory);
        if (conflict == null) return true;

        var targetMemes = App.DataEngine.GetMemes(conflict).ToList();
        var conflicts = new List<(MemeModel src, MemeModel dst)>();
        foreach (var m in memes)
        {
            if (m.Category.Equals(conflict, StringComparison.OrdinalIgnoreCase)) continue;
            var dst = targetMemes.FirstOrDefault(x => x.Hash.Equals(m.Hash, StringComparison.OrdinalIgnoreCase));
            if (dst != null) conflicts.Add((m, dst));
        }

        static string Label(MemeModel m)
        {
            var s = string.IsNullOrWhiteSpace(m.Title) ? m.FileName : m.Title;
            return s.Length > MoveConflictLabelMaxLen ? s.Substring(0, MoveConflictLabelMaxLen) + "..." : s;
        }
        foreach (var (src, dst) in conflicts)
            Log($"[移动冲突] 阻止移动: \"{Label(src)}\"({src.FileName}, 源分类=\"{src.Category}\") -> \"{Label(dst)}\"({dst.FileName}, 目标分类=\"{conflict}\")");

        var lines = new List<string> { $"分类\"{conflict}\"已经存在相同的图片", "", "冲突明细:" };
        foreach (var (src, dst) in conflicts)
            lines.Add($"\"{Label(src)}\" -> \"{Label(dst)}\"");

        await Dialogs.ShowMessageAsync(this, "移动图片失败", string.Join("\n", lines));
        return false;
    }

    private async void MoveMemeToCategory(MemeViewModel vm, string targetName)
    {
        List<MemeViewModel> toMove;
        var selected = (MemeGridView.SelectedItems?.Cast<MemeViewModel>() ?? []).ToList();
        if (_editMode && selected.Count > 0)
            toMove = selected;
        else
            toMove = new List<MemeViewModel> { vm };

        var models = toMove.Select(m => m.Model).ToList();
        if (!await GuardMoveConflictAsync(models, targetName))
            return;

        await App.DataEngine.MoveMemesToCategoryAsync(models, targetName);
        Log($"右键移动 {toMove.Count} 张图片到分类「{targetName}」");
        RemoveFromCurrentView(models);
        UpdateCategoryCounts();
    }

    private void SelectAllButton_Click(object? sender, RoutedEventArgs e) => ToggleSelectAll();

    private async void BatchImportButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await PickerHelper.PickMultipleFilesAsync(
            this,
            ("图片", ".png"), ("图片", ".jpg"), ("图片", ".jpeg"),
            ("图片", ".gif"), ("图片", ".webp"), ("图片", ".bmp"));

        if (files.Count == 0) return;
        foreach (var file in files)
        {
            var (imported, _) = await App.DataEngine.ImportMemeAsync(file, _currentCategory);
            if (imported != null)
                InsertMemeAtFront(new MemeViewModel(imported));
        }
        UpdateCategoryCounts();
    }

    private List<MemeViewModel> SelectedMemeViewModels()
        => (MemeGridView.SelectedItems?.Cast<MemeViewModel>() ?? []).ToList();

    private async void BatchExportButton_Click(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedMemeViewModels();
        if (selected.Count == 0) return;
        var folder = await PickerHelper.PickFolderAsync(this);
        if (folder == null) return;
        await App.DataEngine.ExportMemesAsync(selected.Select(m => m.Model), folder);
    }

    private async void DeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedMemeViewModels();
        if (selected.Count == 0) return;
        var confirm = await Dialogs.ShowConfirmAsync(this, "删除确认", $"确定要删除选中的 {selected.Count} 个表情吗？");
        if (!confirm) return;

        await App.DataEngine.DeleteMemesAsync(selected.Select(m => m.Model));
        RemoveFromCurrentView(selected.Select(m => m.Model));
        MemeGridView?.SelectedItems?.Clear();
        UpdateCategoryCounts();
    }

    private void BatchMoveButton_Click(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedMemeViewModels();
        if (selected.Count == 0) return;
        var models = selected.Select(m => m.Model).ToList();
        foreach (var cat in _categoryList)
        {
            if (_currentCategory.Equals(cat.Name, StringComparison.OrdinalIgnoreCase)) continue;
            var targetName = cat.Name;
            Task.Run(async () =>
            {
                if (!await GuardMoveConflictAsync(models, targetName)) return;
                await App.DataEngine.MoveMemesToCategoryAsync(models, targetName);
                Log($"批量移动 {selected.Count} 张图片到分类「{targetName}」");
                Dispatcher.UIThread.Post(() =>
                {
                    RemoveFromCurrentView(models);
                    UpdateCategoryCounts();
                });
            });
        }
    }

    private void SettingsPanel_Finished(object? sender, EventArgs e)
    {
        if (SettingsButton.Flyout is Flyout flyout)
            flyout.Hide();
    }

    private void SettingsFlyout_Opened(object? sender, EventArgs e)
    {
        // 每次打开重新填充控件并重置保存标志（Flyout 复用同一 SettingsPanel 实例）。
        SettingsPanelInstance.PrepareShow();
    }

    private async void SettingsFlyout_Closed(object? sender, EventArgs e)
    {
        // 无论以何种方式关闭，统一在此保存配置，避免遗漏且规避卸载竞态。
        await SettingsPanelInstance.SaveAsync();
    }

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
        => await RefreshDataAsync();

    private async Task RefreshDataAsync()
    {
        Log("刷新：重新读取数据目录");
        await App.DataEngine.InitializeAsync();
        LoadCategories();
    }

    public void OpenSettings()
    {
        ShowWindow(activate: true);
        if (SettingsButton.Flyout is Flyout flyout)
            flyout.ShowAt(SettingsButton);
    }

    public void ShowAndActivate() => ShowWindow(activate: true);

    public void ShowWindow(bool activate)
    {
        if (NativeMethods.IsWindowVisible(_hWnd) && !NativeMethods.IsIconic(_hWnd))
        {
            Log("[窗口] 显示：已可见，跳过");
            _isVisible = true;
            return;
        }

        if (NativeMethods.IsIconic(_hWnd))
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_RESTORE);
        else if (activate)
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_SHOW);
        else
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_SHOWNOACTIVATE);

        if (activate)
            NativeMethods.SetForegroundWindow(_hWnd);
        if (_topMost)
            ApplyTopMost(true);

        _isVisible = true;
        SetMemeViewVisible(true);
        ResumeWindowInteractions();
        Log($"[窗口] 显示完成 (activate={activate})");
    }

    private void HideWindow()
    {
        if (!NativeMethods.IsWindowVisible(_hWnd) && !NativeMethods.IsIconic(_hWnd))
        {
            Log("[窗口] 隐藏：已隐藏，跳过");
            _isVisible = false;
            return;
        }

        NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_HIDE);
        _isVisible = false;
        SetMemeViewVisible(false);
        SuspendWindowInteractions(closing: false);
        Log("[窗口] 隐藏完成 (SW_HIDE)");
    }

    private void ToggleWindow()
    {
        bool iconic = NativeMethods.IsIconic(_hWnd);
        bool visible = NativeMethods.IsWindowVisible(_hWnd) && !iconic;
        Log($"[窗口] 切换：当前可见={visible}");
        if (visible)
            HideWindow();
        else
            ShowWindow(activate: false);
    }

    public void RequestExit()
    {
        SaveWindowSize();
        _allowClose = true;
        _isClosing = true;
        Close();
    }

    public void StartHidden() => HideWindow();

    public void ShowWithoutActivate() => ShowWindow(activate: false);

    private void TopMostToggle_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        bool on = TopMostToggle.IsChecked == true;
        _topMost = on;
        ApplyTopMost(on);
        Log(on ? "[置顶] 已开启置顶" : "[置顶] 已关闭置顶");
    }

    private void SetTaskbarIcon()
    {
        try
        {
            var hIcon = LoadAppIcon();
            if (hIcon == IntPtr.Zero) return;
            NativeMethods.SendMessage(_hWnd, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_SMALL, hIcon);
            NativeMethods.SendMessage(_hWnd, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_BIG, hIcon);
        }
        catch (Exception ex) { Logger.Log($"[MemeManager] 设置窗口图标失败: {ex}"); }
    }

    private IntPtr LoadAppIcon()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            Path.Combine(AppContext.BaseDirectory, "AppIcon.ico"),
            Path.Combine(AppContext.BaseDirectory, "..", "Assets", "AppIcon.ico"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                var h = NativeMethods.LoadImage(IntPtr.Zero, path, NativeMethods.IMAGE_ICON, 0, 0,
                    NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
                if (h != IntPtr.Zero) return h;
            }
        }
        Logger.Log("[MemeManager] 未找到 AppIcon.ico");
        return IntPtr.Zero;
    }

    private DispatcherTimer? _searchDebounceTimer;

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick -= SearchDebounce_Tick;
        _searchDebounceTimer.Tick += SearchDebounce_Tick;
        _searchDebounceTimer.Start();
    }

    private void SearchDebounce_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer?.Stop();
        RefreshMemes();
    }

    private async void Root_KeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.V)
        {
            if (!_isActive) return;
            e.Handled = true;
            await PasteFromClipboardViaShortcutAsync();
            return;
        }
        if (ctrl && e.Key == Key.F)
        {
            e.Handled = true;
            SearchBox.Focus();
            return;
        }
        if (ctrl && e.Key == Key.N)
        {
            e.Handled = true;
            await ShowAddCategoryDialog();
            return;
        }
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            _ = RefreshDataAsync();
            return;
        }
        if (ctrl && e.Key == Key.A)
        {
            ToggleSelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F2)
        {
            if (CategoryList.SelectedItem is CategoryViewModel selCat)
            {
                e.Handled = true;
                await ShowRenameCategoryDialog(selCat);
            }
            return;
        }
        if (e.Key == Key.Delete)
        {
            if (CategoryList.SelectedItem is CategoryViewModel selCat)
            {
                e.Handled = true;
                await DeleteCategoryConfirmed(selCat);
            }
            return;
        }

        if (!_editMode) return;
        if (e.Key == Key.Escape) { ExitEditMode(); e.Handled = true; }
        else if (e.Key == Key.Enter) { ExitEditMode(); e.Handled = true; }
    }

    private void ToggleSelectAll()
    {
        if (!_editMode) return;
        if (MemeGridView == null) return;
        bool allSelected = MemeGridView.SelectedItems!.Count == _memeList.Count && _memeList.Count > 0;
        if (allSelected)
            MemeGridView.SelectedItems!.Clear();
        else
            foreach (var m in _memeList) if (!MemeGridView.SelectedItems!.Contains(m)) MemeGridView.SelectedItems!.Add(m);
        if (SelectAllButton != null)
            SelectAllButton.Content = allSelected ? "全选" : "取消全选";
    }

    private async Task PasteFromClipboardViaShortcutAsync()
    {
        try
        {
            var clipboard = (this as TopLevel)?.Clipboard;
            if (clipboard == null) return;
            var data = await clipboard.TryGetDataAsync();
            if (data == null) { Log("[粘贴] 触发了 Ctrl+V，但剪贴板为空"); return; }

            bool hasBitmap = data.Contains(DataFormat.Bitmap);
            bool hasFiles = data.Contains(DataFormat.File);
            if (!hasBitmap && !hasFiles)
            {
                Log("[粘贴] 剪贴板非图片/图片路径类内容，已忽略");
                return;
            }

            var category = await PromptCategoryForPasteAsync();
            if (category == null) return;

            if (hasFiles)
            {
                var paths = ((await data.TryGetFilesAsync()) ?? Array.Empty<IStorageItem>())
                    .Select(f => f.TryGetLocalPath())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Select(p => p!)
                    .ToList();
                int importedCount = 0;
                foreach (var p in paths)
                {
                    if (IsImage(Path.GetExtension(p)))
                    {
                        var (imported, dup) = await App.DataEngine.ImportMemeAsync(p, category);
                        if (imported != null) { importedCount++; if (category.Equals(_currentCategory, StringComparison.OrdinalIgnoreCase)) InsertMemeAtFront(new MemeViewModel(imported)); }
                    }
                }
                if (importedCount > 0) UpdateCategoryCounts();
            }
            else if (hasBitmap)
            {
                var bmp = await data.TryGetBitmapAsync();
                if (bmp != null)
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"meme_{Guid.NewGuid():N}.png");
                    try
                    {
                        using var ms = new MemoryStream();
                        bmp.Save(ms, new PngBitmapEncoderOptions());
                        File.WriteAllBytes(tempPath, ms.ToArray());
                        var (imported, dup) = await App.DataEngine.ImportMemeAsync(tempPath, category);
                        if (imported != null && category.Equals(_currentCategory, StringComparison.OrdinalIgnoreCase))
                            InsertMemeAtFront(new MemeViewModel(imported));
                        UpdateCategoryCounts();
                    }
                    catch (Exception ex) { Log("[粘贴] 位图保存失败: " + ex.Message); }
                    finally { try { File.Delete(tempPath); } catch { } }
                }
            }
        }
        catch (Exception ex) { Log("[粘贴] PasteFromClipboardViaShortcutAsync 失败: " + ex.Message); }
    }

    private void ExitEditMode()
    {
        _editMode = false;
        EditButton.Content = "修改";
        BatchBar.IsVisible = false;
        MemeGridView?.SelectedItems?.Clear();
        MemeGridView!.SelectionMode = SelectionMode.Single;
        _lastShiftAnchor = -1;
        SelectAllButton.Content = "全选";
        SetSelectionBoxVisible(false);
        foreach (var vm in _memeList) vm.IsSelected = false;
    }

    private void SetSelectionBoxVisible(bool visible)
    {
        if (_isClosing) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing) return;
            foreach (var item in MemeGridView.Items)
            {
                if (MemeGridView.ContainerFromItem(item!) is ListBoxItem container)
                {
                    var box = container.FindControl<CheckBox>("SelectionCheckBox");
                    if (box != null) box.IsVisible = visible;
                }
            }
        });
    }

    private async Task<string?> PromptCategoryForPasteAsync()
    {
        if (_pasteDialogOpen) { Log("[剪贴板] 分类对话框重入，跳过"); return null; }
        _pasteDialogOpen = true;
        try
        {
            var name = await Dialogs.ShowInputAsync(this, "粘贴图片到分类", "输入分类名称（不存在则新建）", _currentCategory);
            if (name == null) { Log("[剪贴板] 取消粘贴"); return null; }
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name)) { Log("[剪贴板] 分类名为空，取消粘贴"); return null; }
            if (!_categoryList.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                await App.DataEngine.AddCategoryAsync(name);
                _categoryList.Add(new CategoryViewModel(name, 0));
                Log($"[剪贴板] 新建分类 {name}");
            }
            return name;
        }
        finally { _pasteDialogOpen = false; }
    }

    private static bool IsImage(string ext) =>
        ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    // ---------- 热键 / 窗口过程 ----------
    private IntPtr NewWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_MOUSEACTIVATE)
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);

        if (uMsg == NativeMethods.WM_ACTIVATE)
        {
            int state = (int)wParam & 0xFFFF;
            _isActive = state != NativeMethods.WA_INACTIVE;
            if (lParam != IntPtr.Zero) _prevActiveHwnd = lParam;
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        if (uMsg == NativeMethods.WM_SYSCOMMAND)
        {
            int cmd = (int)wParam & 0xFFF0;
            Log($"WM_SYSCOMMAND: cmd={cmd:X4}, _isVisible(before)={_isVisible}");
            if (cmd == NativeMethods.SC_MINIMIZE)
            {
                HideWindow();
                Log($"  _isVisible(after)={_isVisible}");
                return IntPtr.Zero;
            }
            else if (cmd == NativeMethods.SC_RESTORE)
            {
                if (_topMost) ApplyTopMost(true);
                ShowWindow(activate: false);
                Log($"  _isVisible(after)={_isVisible}");
                return IntPtr.Zero;
            }
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        if (uMsg == NativeMethods.WM_HOTKEY && (int)wParam == HOTKEY_ID)
        {
            ToggleWindow();
            return IntPtr.Zero;
        }

        if (uMsg == NativeMethods.WM_CLOSE)
        {
            if (!_allowClose)
            {
                Log("WM_CLOSE: 仅隐藏窗口（后台保留）");
                HideWindow();
                return IntPtr.Zero;
            }
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        if (uMsg == NativeMethods.WM_DROPFILES)
        {
            HandleDropFiles(wParam);
            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ApplyTopMost(bool topMost)
    {
        NativeMethods.SetWindowPos(_hWnd, topMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
    }

    private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == _hWnd && idObject == 0 && _topMost && !_isClosing)
            ApplyTopMost(true);
    }

    private void SuspendWindowInteractions(bool closing)
    {
        if (closing) _isClosing = true;
        Log($"[防护] SuspendWindowInteractions: closing={closing}, _isVisible={_isVisible}");
        _fgTimer?.Stop();
    }

    private void ResumeWindowInteractions()
    {
        if (_isClosing) return;
        Log($"[防护] ResumeWindowInteractions: _isVisible={_isVisible}, _editMode={_editMode}");
        Log("[防护] 已恢复窗口交互");
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        SuspendWindowInteractions(closing: true);
        NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_ID);
        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
    }

    private void RegisterConfiguredHotKey()
    {
        NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_ID);
        var cfg = App.DataEngine.Config;
        NativeMethods.RegisterHotKey(_hWnd, HOTKEY_ID, cfg.HotKeyModifiers, cfg.HotKeyVk);
    }

    public void ApplyHotKeyConfig(uint modifiers, ushort vk)
    {
        App.DataEngine.Config.HotKeyModifiers = modifiers;
        App.DataEngine.Config.HotKeyVk = vk;
        RegisterConfiguredHotKey();
        _ = App.DataEngine.SaveConfigAsync();
    }

    public static string HotKeyText(uint modifiers, ushort vk)
    {
        var parts = new List<string>();
        if ((modifiers & 0x8) != 0) parts.Add("Win");
        if ((modifiers & 0x1) != 0) parts.Add("Alt");
        if ((modifiers & 0x2) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x4) != 0) parts.Add("Shift");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(ushort vk)
    {
        switch (vk)
        {
            case 0x41: return "A"; case 0x42: return "B"; case 0x43: return "C";
            case 0x44: return "D"; case 0x45: return "E"; case 0x46: return "F";
            case 0x47: return "G"; case 0x48: return "H"; case 0x49: return "I";
            case 0x4A: return "J"; case 0x4B: return "K"; case 0x4C: return "L";
            case 0x4D: return "M"; case 0x4E: return "N"; case 0x4F: return "O";
            case 0x50: return "P"; case 0x51: return "Q"; case 0x52: return "R";
            case 0x53: return "S"; case 0x54: return "T"; case 0x55: return "U";
            case 0x56: return "V"; case 0x57: return "W"; case 0x58: return "X";
            case 0x59: return "Y"; case 0x5A: return "Z";
            case 0x30: return "0"; case 0x31: return "1"; case 0x32: return "2";
            case 0x33: return "3"; case 0x34: return "4"; case 0x35: return "5";
            case 0x36: return "6"; case 0x37: return "7"; case 0x38: return "8";
            case 0x39: return "9";
            case >= 0x70 and <= 0x87: return "F" + (vk - 0x6F);
        }
        uint scan = NativeMethods.MapVirtualKey(vk, 0);
        bool extended = vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
            or 0x2D or 0x2E or 0x2F or 0x6A or 0x6B or 0x6C or 0x6D or 0xA3 or 0xA4 or 0xA5;
        int lParam = ((int)scan << 16) | (extended ? 0x01000000 : 0);
        var sb = new System.Text.StringBuilder(64);
        if (NativeMethods.GetKeyNameTextW(lParam, sb, sb.Capacity) > 0 && sb.Length > 0)
            return sb.ToString().Replace(" (数字键盘)", "").Replace(" (小键盘)", "").Trim();
        return vk switch
        {
            0xBE => ".", 0xBC => ",", 0xBB => "=", 0xBD => "-",
            0xBA => ";", 0xDE => "'", 0xC0 => "`", 0xDB => "[",
            0xDD => "]", 0xDC => "\\", 0xE2 => "\\",
            _ => "0x" + vk.ToString("X2")
        };
    }
}
