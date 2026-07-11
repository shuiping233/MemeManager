using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using MemeManager.Data;
using MemeManager.Models;
using MemeManager.ViewModels;

namespace MemeManager;

public sealed partial class MainWindow : Window
{
    private readonly IntPtr _hWnd;
    private bool _isVisible = true;
    private const int HOTKEY_ID = 9001;
    private const uint SUBCLASS_ID = 101;

    private readonly NativeMethods.SUBCLASSPROC _subclassProc;

    private readonly ObservableCollection<MemeViewModel> _memeList = new();
    private readonly ObservableCollection<CategoryViewModel> _categoryList = new();

    private string _currentCategory = string.Empty;
    private bool _editMode;

    // 内部拖拽移动：当前拖拽的 meme 模型列表（非空即表示内部拖拽，区别于外部导入）
    private List<MemeModel>? _draggingMemes;

    // 记录本窗口激活前的前台窗口（通常是正在聊天的目标应用），用于粘贴时回投 Ctrl+V
    private IntPtr _prevActiveHwnd;
    private IntPtr _lastExternalFg;
    private bool _isActive;

    // 多选模式：Shift 连续选择的锚点（在 _memeList 中的索引）
    private int _lastShiftAnchor = -1;

    // 防止粘贴导入的分类对话框重入
    private bool _pasteDialogOpen;

    // 是否允许真正关闭窗口（仅托盘“退出”时置 true；普通点 X 只隐藏）
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();

        _hWnd = WindowNative.GetWindowHandle(this);

        SetTaskbarIcon();

        int exStyle = NativeMethods.GetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOPMOST);

        RegisterConfiguredHotKey();

        _subclassProc = NewWindowProc;
        NativeMethods.SetWindowSubclass(_hWnd, _subclassProc, SUBCLASS_ID, IntPtr.Zero);

        // 让窗口在 Win32 层面也能接收拖入的文件（QQ 等来源可能只发文件，不走 XAML DataPackage）
        NativeMethods.DragAcceptFiles(_hWnd, true);

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));

        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlappedPresenter)
            overlappedPresenter.IsAlwaysOnTop = true;

        CategoryList.ItemsSource = _categoryList;
        MemeGridView.ItemsSource = _memeList;

        // 粘贴图片进窗口
        this.Activated += MainWindow_Activated;
        _clipboardHooked = false;
        HookClipboard();

        Closed += Window_Closed;

        SettingsFlyout.Closed += SettingsFlyout_Closed;

        // 监听选中项变化，自动启用/禁用批量操作按钮
        _memeList.CollectionChanged += OnMemeListChanged;

        // 当我们未激活时，持续记录当前前台窗口（即用户正在用的 QQ 等应用），
        // 点击表情时把 Ctrl+V 投回给它
        var fgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        fgTimer.Tick += (_, _) =>
        {
            if (!_isActive)
            {
                var fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != _hWnd)
                    _lastExternalFg = fg;
            }
        };
        fgTimer.Start();

        // Esc 退出多选模式 / Enter 完成多选模式
        if (this.Content is FrameworkElement root)
        {
            root.KeyDown += Root_KeyDown;
            root.Loaded += (_, _) => { if (this.Content is FrameworkElement r) r.Focus(FocusState.Programmatic); };
        }

        LoadCategories();
    }

    // ---------- 分类 ----------

    private void LoadCategories()
    {
        // 若没有任何分类文件夹，默认创建一个 "Default"
        if (App.DataEngine.GetCategories().Count == 0)
        {
            try { App.DataEngine.AddCategoryAsync("Default").GetAwaiter().GetResult(); }
            catch { }
        }

        _categoryList.Clear();
        foreach (var cat in App.DataEngine.GetCategories())
        {
            int count = App.DataEngine.GetMemes(cat).Count;
            _categoryList.Add(new CategoryViewModel(cat, count));
        }

        // 默认选中上次或第一项
        var last = App.DataEngine.Config.LastCategory;
        var target = _categoryList.FirstOrDefault(c => c.Name == last) ?? _categoryList.FirstOrDefault();
        if (target != null)
        {
            CategoryList.SelectedItem = target;
            _currentCategory = target.Name;
        }

        RefreshMemes();
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is CategoryViewModel cat)
        {
            _currentCategory = cat.Name;
            _ = App.DataEngine.UpdateConfigAsync(c => c.LastCategory = cat.Name);
            RefreshMemes();
        }
    }

    private void CategoryList_ContextRequested(object sender, ContextRequestedEventArgs e)
    {
        // 找到右键所在的分类项
        var cat = FindCategoryFromSource(e.OriginalSource);
        if (cat == null) return;

        Log($"右键分类项: {cat.Name}");

        var flyout = new MenuFlyout();
        var deleteItem = new MenuFlyoutItem { Text = "删除" };
        deleteItem.Click += async (_, __) =>
        {
            var dialog = new ContentDialog
            {
                Title = "删除分类",
                Content = $"确定要删除分类「{cat.Name}」吗？\n该分类下的所有表情与文件夹都会被删除。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                XamlRoot = this.Content.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            bool ok = await App.DataEngine.DeleteCategoryAsync(cat.Name);
            if (ok)
            {
                // 从 UI 列表移除
                for (int i = _categoryList.Count - 1; i >= 0; i--)
                    if (_categoryList[i].Name.Equals(cat.Name, StringComparison.OrdinalIgnoreCase))
                        _categoryList.RemoveAt(i);

                // 若删除的是当前分类，切换到第一项（若有）
                if (_currentCategory.Equals(cat.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _currentCategory = _categoryList.FirstOrDefault()?.Name ?? string.Empty;
                    CategoryList.SelectedItem = _categoryList.FirstOrDefault();
                }
                RefreshMemes();
            }
        };
        flyout.Items.Add(deleteItem);
        // 不传坐标，避免边界坐标导致 ShowAt 失败
        flyout.ShowAt((FrameworkElement)e.OriginalSource);
    }

    private CategoryViewModel? FindCategoryFromSource(object? source)
    {
        var element = source as DependencyObject;
        while (element != null)
        {
            if (element is FrameworkElement fe && fe.DataContext is CategoryViewModel vm)
                return vm;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    // 拖拽图片到分类列表：仅接受内部移动，并高亮可放置
    private void CategoryList_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingMemes != null && _draggingMemes.Count > 0)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "移动到该分类";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void CategoryListItem_Drop(object sender, DragEventArgs e)
    {
        if (_draggingMemes == null || _draggingMemes.Count == 0)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }

        // 目标分类 = 被拖放到的那个分类项（sender 即该项模板根 Grid，DataContext 为分类）
        var targetCat = (sender as FrameworkElement)?.DataContext as CategoryViewModel;
        Log($"[分类Drop] 触发, 目标分类={targetCat?.Name ?? "(无)"}");
        if (targetCat == null) return;

        var memes = _draggingMemes;
        _draggingMemes = null;

        int moved = memes.Count(m => !m.Category.Equals(targetCat.Name, StringComparison.OrdinalIgnoreCase));
        if (moved > 0)
        {
            await App.DataEngine.MoveMemesToCategoryAsync(memes, targetCat.Name);
            Log($"Drop: 内部移动 {moved} 张图片到分类「{targetCat.Name}」");
            RefreshMemes();
            UpdateCategoryCounts();
        }
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private void CategoryListItem_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingMemes != null && _draggingMemes.Count > 0)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "移动到该分类";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void AddCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "输入新分类名称" };
        var dialog = new ContentDialog
        {
            Title = "新增分类",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            XamlRoot = this.Content.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var name = box.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            bool added = await App.DataEngine.AddCategoryAsync(name);
            if (added)
            {
                _categoryList.Add(new CategoryViewModel(name, 0));
                CategoryList.SelectedItem = _categoryList.Last();
            }
        }
    }

    // ---------- 表情渲染 ----------

    private void RefreshMemes()
    {
        _memeList.Clear();
        var keyword = SearchBox.Text?.Trim();
        var memes = App.DataEngine.GetMemes(
            string.IsNullOrWhiteSpace(_currentCategory) ? null : _currentCategory,
            string.IsNullOrWhiteSpace(keyword) ? null : keyword);

        foreach (var m in memes)
        {
            var vm = new MemeViewModel(m);
            vm.ShowSelectionUI = _editMode;
            _memeList.Add(vm);
        }

        UpdateCategoryCounts();
    }

    private void UpdateCategoryCounts()
    {
        foreach (var c in _categoryList)
            c.Count = App.DataEngine.GetMemes(c.Name).Count;
    }

    // 根据当前是否有选中项，启用/禁用批量操作按钮（无选中时灰掉且不可点）
    private void UpdateBatchButtons()
    {
        bool anySelected = _memeList.Any(m => m.IsSelected);
        if (BatchExportButton != null) BatchExportButton.IsEnabled = anySelected;
        if (BatchMoveButton != null) BatchMoveButton.IsEnabled = anySelected;
        if (DeleteButton != null) DeleteButton.IsEnabled = anySelected;
    }

    private void OnMemeListChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (MemeViewModel vm in e.NewItems)
                vm.PropertyChanged += OnMemeVmPropertyChanged;
        if (e.OldItems != null)
            foreach (MemeViewModel vm in e.OldItems)
                vm.PropertyChanged -= OnMemeVmPropertyChanged;
        UpdateBatchButtons();
    }

    private void OnMemeVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemeViewModel.IsSelected))
            UpdateBatchButtons();
    }

    // ---------- 修改模式 ----------

    private void EditButton_Click(object sender, RoutedEventArgs e)
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
            EditButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            BatchBar.Visibility = Visibility.Visible;
            // 编辑模式开启内置重排：落点由 WinUI 自己算准
            MemeGridView.CanReorderItems = true;
            // 多选拖拽重排需要 GridView 原生选中(SelectedItems)，否则 WinUI 只重排被按下的单个项
            MemeGridView.SelectionMode = ListViewSelectionMode.Multiple;
            foreach (var m in _memeList) m.ShowSelectionUI = true;
        }
    }

    // ---------- 点击表情（非修改模式 = 粘贴；多选模式 = 切换选中） ----------
    // 用 Tapped 而非 PointerPressed：拖拽(CanDrag)会取消 Tapped，避免“先单击粘贴一次、再拖出又粘贴一次”

    private async void MemeItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MemeViewModel clicked)
        {
            Log("MemeItem_Tapped: 取不到 MemeViewModel, sender=" + sender?.GetType().Name);
            return;
        }

        int index = _memeList.IndexOf(clicked);

        // ---- 多选模式：切换原生选中（驱动 SelectedItems，WinUI 才能整组拖拽重排） ----
        if (_editMode)
        {
            bool shift = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (shift && _lastShiftAnchor >= 0 && index >= 0)
            {
                int a = Math.Min(_lastShiftAnchor, index);
                int b = Math.Max(_lastShiftAnchor, index);
                bool targetState = !MemeGridView.SelectedItems.Contains(clicked);
                for (int i = a; i <= b; i++)
                {
                    var vm = _memeList[i];
                    if (targetState) { if (!MemeGridView.SelectedItems.Contains(vm)) MemeGridView.SelectedItems.Add(vm); }
                    else MemeGridView.SelectedItems.Remove(vm);
                }
            }
            else
            {
                if (MemeGridView.SelectedItems.Contains(clicked))
                    MemeGridView.SelectedItems.Remove(clicked);
                else
                    MemeGridView.SelectedItems.Add(clicked);
            }

            _lastShiftAnchor = index;
            Log($"单击(多选模式): 切换选中 {clicked.Title}, 已选={MemeGridView.SelectedItems.Count} (shift={shift})");
            return;
        }

        // ---- 普通模式：发送图片 ----
        IntPtr liveFg = NativeMethods.GetForegroundWindow();
        IntPtr target = IntPtr.Zero;
        if (_lastExternalFg != IntPtr.Zero && _lastExternalFg != _hWnd)
            target = _lastExternalFg;
        else if (_prevActiveHwnd != IntPtr.Zero && _prevActiveHwnd != _hWnd)
            target = _prevActiveHwnd;
        else if (liveFg != IntPtr.Zero && liveFg != _hWnd)
            target = liveFg;

        Log($"单击(发送模式): 发送图片 {clicked.Title} 到前台窗口 target={target}");
        IgnoreNextClipboardChange();
        await PasteService.OutputMemeToCursorAsync(clicked.LocalPath, target);
        await App.DataEngine.IncrementUsageAsync(clicked.Hash);
    }

    // GridView 原生选中变化 → 同步自定义 IsSelected（供 UI 勾选 + 批量操作读取）
    private void MemeGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_editMode) return;
        foreach (var item in e.AddedItems)
            if (item is MemeViewModel vm) vm.IsSelected = true;
        foreach (var item in e.RemovedItems)
            if (item is MemeViewModel vm) vm.IsSelected = false;
        UpdateBatchButtons();
    }

    // ---------- 拖拽：拖入导入 / 拖出到外部输入框 ----------

    private static void Log(string msg) => Logger.Log($"[MemeManager] {msg}");

    private void MemeGridView_DragOver(object sender, DragEventArgs e)
    {
        // 接受一切拖拽对象，确保 Drop 能触发（后面在 Drop 里再筛选图片）
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = false;
    }

    // ---------- 拖拽项事件（WinUI 内置重排 / 拖出） ----------

    // 项开始被拖出（编辑模式或非编辑模式都会触发，因为 CanDragItems=True）。
    // 记录被拖项 + 设 StorageItems（供拖到文件管理器/输入框时复制）。
    private void MemeGridView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var draggedVms = e.Items.Cast<MemeViewModel>().ToList();
        if (draggedVms.Count == 0) return;

        // 编辑模式多选：WinUI 内置重排整组依据的是 GridView.SelectedItems，
        // 所以被拖组 = 当前原生选中项（若拖动的项是选中组一员）；否则只拖当前项。
        List<MemeViewModel> group;
        var selected = MemeGridView.SelectedItems.Cast<MemeViewModel>().ToList();
        if (_editMode && selected.Count > 0 && draggedVms.Any(v => selected.Contains(v)))
            group = selected;
        else
            group = draggedVms;

        _draggingMemes = group.Select(m => m.Model).ToList();
        Log($"DragItemsStarting: 拖出 {_draggingMemes.Count} 张图片 (首项 {group[0].Title})");

        // 设文件以便拖到外部时复制
        try
        {
            var files = group
                .Select(v => StorageFile.GetFileFromPathAsync(v.LocalPath).AsTask().Result)
                .ToArray();
            e.Data.SetStorageItems(files);
            // 内置重排(CanReorderItems)需要 Move 语义才会真正移动集合；
            // 拖到外部(文件管理器/输入框)时 WinUI 会按目标能力自动回退成 Copy。
            e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        }
        catch (Exception ex)
        {
            Log("DragItemsStarting: 设 StorageItems 失败: " + ex.Message);
        }
    }

    // 拖拽完成。编辑模式下 WinUI 内置重排已把 _memeList 真正重排好，这里读顺序写回 Priority。
    private async void MemeGridView_DragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
    {
        Log($"DragItemsCompleted: _draggingMemes={( _draggingMemes?.Count ?? 0 )}, _editMode={_editMode}, DropResult={e.DropResult}");
        if (_draggingMemes == null) return;

        if (_editMode)
        {
            // WinUI 内置重排在 DropResult==Move 时已移动 ItemsSource(_memeList)。
            // 打印前后顺序确认。
            var currentOrder = _memeList.Select(m => m.FileName).ToList();
            Log($"DragItemsCompleted: DropResult={e.DropResult}, 当前_memeList顺序=[{string.Join(",", currentOrder)}]");

            var itemsOrder = new List<string>();
            for (int i = 0; i < MemeGridView.Items.Count; i++)
                if (MemeGridView.Items[i] is MemeViewModel vm) itemsOrder.Add(vm.FileName);
            Log($"DragItemsCompleted: GridView.Items顺序=[{string.Join(",", itemsOrder)}]");

            var ordered = itemsOrder.Count == currentOrder.Count && itemsOrder.Count > 0 ? itemsOrder : currentOrder;

            await App.DataEngine.ReorderMemesAsync(_currentCategory, ordered);
            Log($"DragItemsCompleted: 编辑模式重排写回 {ordered.Count} 张图片到分类「{_currentCategory}」");
            RefreshMemes();
            UpdateCategoryCounts();
        }

        _draggingMemes = null;
    }

    private async void MemeGridView_Drop(object sender, DragEventArgs e)
    {
        Log("Drop 事件触发");
        var view = e.DataView;

        // 内部拖拽
        if (_draggingMemes != null && _draggingMemes.Count > 0)
        {
            var memes = _draggingMemes;
            _draggingMemes = null;

            // 编辑模式下，网格内重排交给 WinUI 内置重排（CanReorderItems），
            // 落点在 DragItemsCompleted 里读新顺序写回 Priority，这里不处理。
            if (_editMode)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                return;
            }

            // 非编辑模式：拖到网格（当前分类）视为移动到当前分类（原地，通常无意义但保持行为一致）
            int moved = memes.Count(m => !m.Category.Equals(_currentCategory, StringComparison.OrdinalIgnoreCase));
            if (moved > 0)
            {
                await App.DataEngine.MoveMemesToCategoryAsync(memes, _currentCategory);
                Log($"Drop: 内部移动 {moved} 张图片到分类「{_currentCategory}」");
                RefreshMemes();
                UpdateCategoryCounts();
            }
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            return;
        }

        // 列出所有可用的数据格式，便于排查 QQ 等特殊来源
        var formats = view.AvailableFormats;
        Log($"Drop: 可用格式数量={formats.Count}");
        foreach (var f in formats)
            Log($"Drop: 格式 = {f}");

        int importedCount = 0;

        // 1) StorageItems（最常见：文件 / QQ 拖出的文件）
        if (view.Contains(StandardDataFormats.StorageItems))
        {
            var items = await view.GetStorageItemsAsync();
            Log($"Drop: StorageItems 共 {items.Count} 个, 目标分类={_currentCategory}");
            foreach (var item in items)
            {
                Log($"Drop: 项 {item.Name} 类型={item.GetType().Name} IsFile={item is StorageFile}");
                if (item is StorageFile file && IsImage(file.FileType))
                {
                    var imported = await App.DataEngine.ImportMemeAsync(file.Path, _currentCategory);
                    if (imported != null) importedCount++;
                }
            }
        }

        // 2) Bitmap（剪贴板/截图类拖拽）
        if (view.Contains(StandardDataFormats.Bitmap))
        {
            Log("Drop: 含 Bitmap，尝试作为图片导入");
            try
            {
                var streamRef = await view.GetBitmapAsync();
                using var stream = await streamRef.OpenReadAsync();
                var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"meme_{Guid.NewGuid():N}.png");
                using (var outStream = System.IO.File.Create(tempPath))
                {
                    await stream.AsStreamForRead().CopyToAsync(outStream);
                }
                var imported = await App.DataEngine.ImportMemeAsync(tempPath, _currentCategory);
                if (imported != null) importedCount++;
                try { System.IO.File.Delete(tempPath); } catch { }
            }
            catch (Exception ex) { Log("Drop: Bitmap 导入失败: " + ex.Message); }
        }

        // 3) Text / Html：看看是不是图片路径或图片链接
        if (view.Contains(StandardDataFormats.Text))
        {
            var text = await view.GetTextAsync();
            Log($"Drop: Text 内容(前200字符)= {text?.Substring(0, Math.Min(200, text?.Length ?? 0))}");
        }
        if (view.Contains(StandardDataFormats.Html))
        {
            var html = await view.GetHtmlFormatAsync();
            Log($"Drop: Html 内容(前200字符)= {html?.Substring(0, Math.Min(200, html?.Length ?? 0))}");
        }

        if (importedCount > 0)
        {
            RefreshMemes();
            UpdateCategoryCounts();
            Log($"Drop: 成功导入 {importedCount} 个图片");
        }
        else
        {
            Log("Drop: 未导入任何图片（已忽略不符合要求的拖拽对象）");
        }
    }

    // ---------- Win32 层拖入文件（WM_DROPFILES）----------

    private void HandleDropFiles(IntPtr hDrop)
    {
        // 注意：此函数在窗口过程(WM_DROPFILES)回调内同步执行，
        // 绝对不能在里面阻塞等待异步(会卡死消息泵)。只同步收集路径，后续异步导入。
        try
        {
            uint count = NativeMethods.DragQueryFile(hDrop, 0xFFFFFFFFu, IntPtr.Zero, 0);
            Log($"WM_DROPFILES: 拖入 {count} 个文件, 目标分类={_currentCategory}");
            var paths = new System.Collections.Generic.List<string>();
            for (uint i = 0; i < count; i++)
            {
                uint len = NativeMethods.DragQueryFile(hDrop, i, IntPtr.Zero, 0);
                if (len == 0) continue;
                IntPtr buf = Marshal.AllocCoTaskMem((int)(len + 1) * 2);
                try
                {
                    NativeMethods.DragQueryFile(hDrop, i, buf, len + 1);
                    string path = Marshal.PtrToStringUni(buf) ?? string.Empty;
                    Log($"WM_DROPFILES: 文件[{i}] = {path}");
                    if (System.IO.File.Exists(path) && IsImage(System.IO.Path.GetExtension(path)))
                        paths.Add(path);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buf);
                }
            }
            NativeMethods.DragFinish(hDrop);

            // 异步导入，不阻塞窗口过程
            _ = ImportDroppedFilesAsync(paths);
        }
        catch (Exception ex)
        {
            Log("WM_DROPFILES 处理失败: " + ex.Message);
        }
    }

    private async Task ImportDroppedFilesAsync(System.Collections.Generic.List<string> paths)
    {
        if (paths.Count == 0) return;
        int importedCount = 0;
        foreach (var path in paths)
        {
            var imported = await App.DataEngine.ImportMemeAsync(path, _currentCategory);
            if (imported != null) importedCount++;
        }

        // 回到 UI 线程刷新
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshMemes();
            UpdateCategoryCounts();
            Log($"WM_DROPFILES: 成功导入 {importedCount} 个图片");
        });
    }

    // ---------- 右键菜单 ----------

    private void MemeItem_ContextRequested(object sender, ContextRequestedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MemeViewModel vm)
        {
            Log("右键单击表情项: " + vm.Title);
            var flyout = new MenuFlyout();
            var deleteItem = new MenuFlyoutItem { Text = "删除" };
            deleteItem.Click += async (_, __) =>
            {
                var dialog = new ContentDialog
                {
                    Title = "删除确认",
                    Content = $"确定要删除「{vm.Title}」吗？",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    XamlRoot = this.Content.XamlRoot
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await App.DataEngine.DeleteMemesAsync(new[] { vm.Model });
                    var item = _memeList.FirstOrDefault(m => m == vm);
                    if (item != null) _memeList.Remove(item);
                    UpdateCategoryCounts();
                }
            };
            flyout.Items.Add(deleteItem);

            var openItem = new MenuFlyoutItem { Text = "打开" };
            openItem.Click += (_, __) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = vm.Model.LocalPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Log($"[打开图片] 失败: {ex.Message}");
                }
            };
            flyout.Items.Add(openItem);

            // 移动到其他分类：子菜单列出所有分类（排除当前所在分类）
            var moveSub = new MenuFlyoutSubItem { Text = "移动到其他分类" };
            var currentCat = vm.Category;
            bool hasTarget = false;
            foreach (var cat in _categoryList)
            {
                if (cat.Name.Equals(currentCat, StringComparison.OrdinalIgnoreCase)) continue;
                hasTarget = true;
                var targetName = cat.Name;
                var moveItem = new MenuFlyoutItem { Text = cat.Name };
                moveItem.Click += async (_, __) =>
                {
                    // 编辑模式且已选中则移动所有选中项，否则只移动当前项
                    List<MemeViewModel> toMove;
                    if (_editMode && vm.IsSelected)
                        toMove = _memeList.Where(m => m.IsSelected).ToList();
                    else
                        toMove = new List<MemeViewModel> { vm };

                    await App.DataEngine.MoveMemesToCategoryAsync(toMove.Select(m => m.Model), targetName);
                    Log($"右键移动 {toMove.Count} 张图片到分类「{targetName}」");
                    RefreshMemes();
                    UpdateCategoryCounts();
                };
                moveSub.Items.Add(moveItem);
            }
            if (hasTarget)
                flyout.Items.Add(moveSub);

            // ContextRequested 的 target 用 fe 自身（不传坐标），避免边界坐标导致 ShowAt 失败
            flyout.ShowAt(fe);
        }
    }

    // ---------- 批量操作 ----------

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleSelectAll();
    }

    private async void BatchImportButton_Click(object sender, RoutedEventArgs e)
    {
        var files = await PickerHelper.PickMultipleFilesAsync(
            this,
            PickerLocationId.PicturesLibrary,
            ("图片", ".png"), ("图片", ".jpg"), ("图片", ".jpeg"),
            ("图片", ".gif"), ("图片", ".webp"), ("图片", ".bmp"));

        if (files.Count == 0) return;

        foreach (var file in files)
        {
            var imported = await App.DataEngine.ImportMemeAsync(file, _currentCategory);
            if (imported != null)
                _memeList.Add(new MemeViewModel(imported));
        }
        UpdateCategoryCounts();
    }

    private async void BatchExportButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _memeList.Where(m => m.IsSelected).ToList();
        if (selected.Count == 0) return;

        var folder = await PickerHelper.PickFolderAsync(this);
        if (folder == null) return;

        await App.DataEngine.ExportMemesAsync(selected.Select(m => m.Model), folder);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _memeList.Where(m => m.IsSelected).ToList();
        if (selected.Count == 0) return;

        var dialog = new ContentDialog
        {
            Title = "删除确认",
            Content = $"确定要删除选中的 {selected.Count} 个表情吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = this.Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await App.DataEngine.DeleteMemesAsync(selected.Select(m => m.Model));
        foreach (var m in selected) _memeList.Remove(m);
        UpdateCategoryCounts();
    }

    // 批量移动到：弹出分类下拉，点击后将选中项移动到该分类
    private void BatchMoveFlyout_Opening(object? sender, object e)
    {
        if (BatchMoveFlyout == null) return;
        BatchMoveFlyout.Items.Clear();

        var selected = _memeList.Where(m => m.IsSelected).ToList();
        if (selected.Count == 0) return;

        bool hasTarget = false;
        foreach (var cat in _categoryList)
        {
            // 跳过当前所在分类（移动过去无意义）
            if (_currentCategory.Equals(cat.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            hasTarget = true;
            var targetName = cat.Name;
            var item = new MenuFlyoutItem { Text = cat.Name };
            item.Click += async (_, __) =>
            {
                await App.DataEngine.MoveMemesToCategoryAsync(selected.Select(m => m.Model), targetName);
                Log($"批量移动 {selected.Count} 张图片到分类「{targetName}」");
                RefreshMemes();
                UpdateCategoryCounts();
            };
            BatchMoveFlyout.Items.Add(item);
        }

        if (!hasTarget)
        {
            BatchMoveFlyout.Items.Add(new MenuFlyoutItem { Text = "（无其他分类）", IsEnabled = false });
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var page = new SettingsPage();
        page.RequestClose += (_, _) => SettingsFlyout.Hide();
        SettingsFlyout.Content = page;
        SettingsFlyout.ShowAt(SettingsButton);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDataAsync();
    }

    /// <summary>重新读取数据目录并重渲染：分类、表情、缩略图全部刷新</summary>
    private async Task RefreshDataAsync()
    {
        Log("刷新：重新读取数据目录");
        await App.DataEngine.InitializeAsync();
        LoadCategories();
        RefreshMemes();
    }

    /// <summary>托盘菜单“设置”：先呼出窗口，再弹设置页</summary>
    public void OpenSettings()
    {
        RestoreAndShow();
        SettingsButton_Click(this, new RoutedEventArgs());
    }

    /// <summary>托盘菜单“显示主窗口”：显示并激活窗口（兼容最小化状态）</summary>
    public void ShowAndActivate()
    {
        RestoreAndShow();
        NativeMethods.SetForegroundWindow(_hWnd);
    }

    /// <summary>
    /// 把窗口从最小化/隐藏状态恢复到可见前台。
    /// 最小化窗口必须用 SW_RESTORE（SW_SHOW 对 iconic 窗口无效）。
    /// </summary>
    private void RestoreAndShow()
    {
        if (NativeMethods.IsIconic(_hWnd))
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_RESTORE);
        else
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_SHOW);

        NativeMethods.SetForegroundWindow(_hWnd);
        _isVisible = true;
        RefreshMemes();
    }

    /// <summary>托盘“退出”：允许真正关闭窗口并退出程序</summary>
    public void RequestExit()
    {
        _allowClose = true;
        this.Close();
    }

    /// <summary>
    /// 手动将图标设到窗口，使独立发布（非 MSIX）时任务栏/标题栏也显示 Logo。
    /// WinUI 3 不会自动从 EXE 图标继承窗口图标，需通过 WM_SETICON 显式设置。
    /// </summary>
    private void SetTaskbarIcon()
    {
        try
        {
            var hIcon = LoadAppIcon();
            if (hIcon == IntPtr.Zero)
                return;

            // 同时设置大/小两套，任务栏用 small，标题栏/alt-tab 用 big
            NativeMethods.SendMessage(_hWnd, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_SMALL, hIcon);
            NativeMethods.SendMessage(_hWnd, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_BIG, hIcon);
        }
        catch (Exception ex)
        {
            Logger.Log($"[MemeManager] 设置窗口图标失败: {ex}");
        }
    }

    private IntPtr LoadAppIcon()
    {
        // 从 exe 运行目录的 AppIcon.ico 文件加载（LoadImage 已验证可用）
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
                var h = NativeMethods.LoadImage(
                    IntPtr.Zero, path, NativeMethods.IMAGE_ICON, 0, 0,
                    NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
                if (h != IntPtr.Zero)
                    return h;
            }
        }

        Logger.Log("[MemeManager] 未找到 AppIcon.ico");
        return IntPtr.Zero;
    }

    private async void SettingsFlyout_Closed(object? sender, object e)
    {
        if (SettingsFlyout.Content is SettingsPage page)
            await page.SaveAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshMemes();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ctrl+V：把剪贴板里的图片导入到当前分类
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == Windows.System.VirtualKey.V)
        {
            e.Handled = true;
            _ = ImportFromClipboardAsync();
            return;
        }

        // F5：刷新（任意模式都可用）
        if (e.Key == Windows.System.VirtualKey.F5)
        {
            e.Handled = true;
            _ = RefreshDataAsync();
            return;
        }

        // Ctrl+A：编辑模式下全选/取消全选
        if (ctrl && e.Key == Windows.System.VirtualKey.A)
        {
            ToggleSelectAll();
            e.Handled = true;
            return;
        }

        if (!_editMode) return;

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ExitEditMode();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ExitEditMode();
            e.Handled = true;
        }
    }

    private void ToggleSelectAll()
    {
        if (!_editMode) return;
        bool allSelected = MemeGridView.SelectedItems.Count == _memeList.Count && _memeList.Count > 0;
        if (allSelected)
            MemeGridView.SelectedItems.Clear();
        else
            foreach (var m in _memeList) if (!MemeGridView.SelectedItems.Contains(m)) MemeGridView.SelectedItems.Add(m);
        if (SelectAllButton != null)
            SelectAllButton.Content = allSelected ? "全选" : "取消全选";
    }

    private async Task ImportFromClipboardAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view == null) return;
            if (!view.Contains(StandardDataFormats.StorageItems) &&
                !view.Contains(StandardDataFormats.Bitmap)) return;

            var category = await PromptCategoryForPasteAsync();
            if (category == null) return;

            var imported = await ImportFromClipboardAsync(view, category);
            if (imported != null && category.Equals(_currentCategory, StringComparison.OrdinalIgnoreCase))
            {
                _memeList.Add(new MemeViewModel(imported));
                UpdateCategoryCounts();
            }
        }
        catch (Exception ex)
        {
            Log("ImportFromClipboardAsync 失败: " + ex.Message);
        }
    }

    private void ExitEditMode()
    {
        _editMode = false;
        EditButton.Content = "修改";
        BatchBar.Visibility = Visibility.Collapsed;
        // 退出编辑模式：关闭内置重排与原生多选
        MemeGridView.CanReorderItems = false;
        MemeGridView.SelectedItems.Clear();
        MemeGridView.SelectionMode = ListViewSelectionMode.None;
        foreach (var m in _memeList)
        {
            m.IsSelected = false;
            m.ShowSelectionUI = false;
        }
        _lastShiftAnchor = -1;
        SelectAllButton.Content = "全选";
        EditButton.Style = null;
    }

    // ---------- 粘贴图片进窗口 ----------

    private bool _clipboardHooked;
    private int _ignoreClipboardDepth;

    public void IgnoreNextClipboardChange() => _ignoreClipboardDepth++;

    private void HookClipboard()
    {
        if (_clipboardHooked) return;
        try
        {
            Clipboard.ContentChanged += Clipboard_ContentChanged;
            _clipboardHooked = true;
        }
        catch { }
    }

    private void Clipboard_ContentChanged(object? sender, object e)
    {
        // 忽略由本程序（如输出表情）触发的剪贴板变更，避免自循环
        if (_ignoreClipboardDepth > 0)
        {
            _ignoreClipboardDepth--;
            return;
        }

        // 仅当窗口可见、且焦点确实在本窗口时（用户主动 Ctrl+V 贴进来）才响应
        if (!_isVisible || !_isActive) return;

        try
        {
            var dataPackageView = Clipboard.GetContent();
            if (dataPackageView == null) return;
            if (!dataPackageView.Contains(StandardDataFormats.Bitmap) &&
                !dataPackageView.Contains(StandardDataFormats.StorageItems)) return;

            // 在 UI 线程弹窗选择分类
           DispatcherQueue.TryEnqueue(async () =>
           {
               var category = await PromptCategoryForPasteAsync();
               if (category == null) return;

               var imported = await ImportFromClipboardAsync(dataPackageView, category);
               if (imported != null && category.Equals(_currentCategory, StringComparison.OrdinalIgnoreCase))
               {
                   _memeList.Add(new MemeViewModel(imported));
                   UpdateCategoryCounts();
               }
           });
        }
        catch { }
    }

    private async Task<string?> PromptCategoryForPasteAsync()
    {
        // 防止高速事件重入：对话框已打开时直接返回
        if (_pasteDialogOpen) return null;
        _pasteDialogOpen = true;

        try
        {
            var box = new TextBox
            {
                PlaceholderText = "输入分类名称（不存在则新建）",
                Text = _currentCategory
            };
            var dialog = new ContentDialog
            {
                Title = "粘贴图片到分类",
                Content = box,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                XamlRoot = this.Content.XamlRoot
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;
            var name = box.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) return null;

            if (!_categoryList.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                await App.DataEngine.AddCategoryAsync(name);
                _categoryList.Add(new CategoryViewModel(name, 0));
            }
            return name;
        }
        finally
        {
            _pasteDialogOpen = false;
        }
    }

    private async Task<MemeModel?> ImportFromClipboardAsync(DataPackageView view, string category)
    {
        try
        {
            if (view.Contains(StandardDataFormats.StorageItems))
            {
                var items = await view.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is StorageFile file && IsImage(file.FileType))
                    {
                        return await App.DataEngine.ImportMemeAsync(file.Path, category);
                    }
                }
            }
            else if (view.Contains(StandardDataFormats.Bitmap))
            {
                var streamRef = await view.GetBitmapAsync();
                using var stream = await streamRef.OpenReadAsync();
                var tempPath = Path.Combine(Path.GetTempPath(), $"meme_{Guid.NewGuid():N}.png");
                using (var outStream = File.Create(tempPath))
                {
                    await stream.AsStreamForRead().CopyToAsync(outStream);
                }
                var imported = await App.DataEngine.ImportMemeAsync(tempPath, category);
                try { File.Delete(tempPath); } catch { }
                return imported;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Paste] 导入失败: {ex.Message}");
        }
        return null;
    }

    private static bool IsImage(string ext) =>
        ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) => HookClipboard();

    // ---------- 热键 / 窗口过程 ----------

    private IntPtr NewWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_MOUSEACTIVATE)
        {
            // 允许点击窗口时正常激活（这样文本框可以输入），
            // 不再返回 MA_NOACTIVATE 以免整个窗口无法获得焦点。
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        if (uMsg == NativeMethods.WM_ACTIVATE)
        {
            // 记录“另一个窗口”的句柄：无论是我们被激活（lParam=被挤掉的窗口）
            // 还是我们失去激活（lParam=新激活的窗口），都能拿到上一次的外部应用，
            // 粘贴时把 Ctrl+V 投回给它。
            int state = (int)wParam & 0xFFFF;
            _isActive = state != NativeMethods.WA_INACTIVE;
            if (lParam != IntPtr.Zero)
            {
                _prevActiveHwnd = lParam;
            }
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        if (uMsg == NativeMethods.WM_SYSCOMMAND)
        {
            // 最小化/还原会改变真实可见性，保持 _isVisible 与实际一致，
            // 否则全局快捷键会因状态错位而需要按两次
            int cmd = (int)wParam & 0xFFF0;
            Log($"WM_SYSCOMMAND: cmd={cmd:X4} (MINIMIZE={NativeMethods.SC_MINIMIZE:X4}, RESTORE={NativeMethods.SC_RESTORE:X4}), _isVisible(before)={_isVisible}");
            if (cmd == NativeMethods.SC_MINIMIZE)
                _isVisible = false;
            else if (cmd == NativeMethods.SC_RESTORE)
                _isVisible = true;
            Log($"  _isVisible(after)={_isVisible}");
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        if (uMsg == NativeMethods.WM_HOTKEY && (int)wParam == HOTKEY_ID)
        {
            Log($"WM_HOTKEY 触发, _isVisible={_isVisible}, IsWindowVisible={NativeMethods.IsWindowVisible(_hWnd)}");
            ToggleWindowVisibility();
            return IntPtr.Zero;
        }

        if (uMsg == NativeMethods.WM_CLOSE)
        {
            // 普通点右上角 X：只隐藏窗口，后台（托盘）继续运行
            if (!_allowClose)
            {
                Log("WM_CLOSE: 仅隐藏窗口（后台保留）");
                NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_HIDE);
                _isVisible = false;
                return IntPtr.Zero;
            }
            // _allowClose=true（托盘退出）时放行，交给默认处理真正关闭
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        if (uMsg == NativeMethods.WM_DROPFILES)
        {
            HandleDropFiles(wParam);
            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ToggleWindowVisibility()
    {
        // 注意：Windows 中“最小化(iconic)”的窗口 IsWindowVisible 仍为 true，
        // 所以必须用 IsIconic 判断最小化，否则会误判为“可见”而执行隐藏。
        bool iconic = NativeMethods.IsIconic(_hWnd);
        bool visible = NativeMethods.IsWindowVisible(_hWnd) && !iconic;
        _isVisible = visible;
        Log($"ToggleWindowVisibility: IsWindowVisible={NativeMethods.IsWindowVisible(_hWnd)}, IsIconic={iconic}, _isVisible→{_isVisible}");

        if (visible)
        {
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_HIDE);
            _isVisible = false;
            Log("  执行 SW_HIDE");
        }
        else
        {
            // 用 SHOWNOACTIVATE 显示：窗口置顶但不抢焦点，
            // 这样用户仍能直接点表情粘贴到原来的应用里
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_SHOWNOACTIVATE);
            RefreshMemes();
            _isVisible = true;
            Log("  执行 SW_SHOWNOACTIVATE");
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_ID);
        if (_clipboardHooked)
            Clipboard.ContentChanged -= Clipboard_ContentChanged;
    }

    // ---------- 全局快捷键 ----------

    private void RegisterConfiguredHotKey()
    {
        NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_ID);
        var cfg = App.DataEngine.Config;
        NativeMethods.RegisterHotKey(_hWnd, HOTKEY_ID, cfg.HotKeyModifiers, cfg.HotKeyVk);
    }

    /// <summary>
    /// 设置页修改快捷键后调用，重新注册并持久化
    /// </summary>
    public void ApplyHotKeyConfig(uint modifiers, ushort vk)
    {
        App.DataEngine.Config.HotKeyModifiers = modifiers;
        App.DataEngine.Config.HotKeyVk = vk;
        RegisterConfiguredHotKey();
        _ = App.DataEngine.SaveConfigAsync();
    }

    /// <summary>
    /// 当前配置的快捷键文本，如 "Ctrl+Alt+." / "Ctrl+B" / "Ctrl+F8"
    /// </summary>
    public static string HotKeyText(uint modifiers, ushort vk)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & 0x8) != 0) parts.Add("Win");
        if ((modifiers & 0x1) != 0) parts.Add("Alt");
        if ((modifiers & 0x2) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x4) != 0) parts.Add("Shift");

        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(ushort vk)
    {
        // 常见按键手动映射（GetKeyNameText 依赖扫描码，部分组合会返回空）
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
            case >= 0x70 and <= 0x87: return "F" + (vk - 0x6F); // F1..F24
        }

        // 其余按键用 GetKeyNameText（正确构造 lParam：扫描码 + 扩展键位）
        uint scan = NativeMethods.MapVirtualKey(vk, 0); // MAPVK_VK_TO_VSC
        // 扩展键（方向键、小键盘、右 Ctrl/Alt 等）需要第 24 位
        bool extended = vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 // 翻页/方向
            or 0x2D or 0x2E or 0x2F or 0x6A or 0x6B or 0x6C or 0x6D or 0xA3 or 0xA4 or 0xA5; // Ins/Del/Home/End + 右修饰键
        int lParam = ((int)scan << 16) | (extended ? 0x01000000 : 0);

        var sb = new System.Text.StringBuilder(64);
        if (NativeMethods.GetKeyNameTextW(lParam, sb, sb.Capacity) > 0 && sb.Length > 0)
        {
            // 去掉可能存在的 “(数字键盘)” 等冗余描述，保留简洁名
            return sb.ToString().Replace(" (数字键盘)", "").Replace(" (小键盘)", "").Trim();
        }

        // OEM / 标点等：用 OEM 映射表自行推断
        return vk switch
        {
            0xBE => ".", 0xBC => ",", 0xBB => "=", 0xBD => "-",
            0xBA => ";", 0xDE => "'", 0xC0 => "`", 0xDB => "[",
            0xDD => "]", 0xDC => "\\", 0xE2 => "\\",
            _ => "0x" + vk.ToString("X2")
        };
    }
}
