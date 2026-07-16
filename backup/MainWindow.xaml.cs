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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    private Microsoft.UI.Windowing.AppWindow? _appWindow;
    private bool _isVisible = true;
    private const int HOTKEY_ID = 9001;
    private const uint SUBCLASS_ID = 101;

    // 拖拽到分类列表报错时的显示Title或者文件名的最大长度，过长则截断显示
    private const int MoveConflictLabelMaxLen = 32;
    

    private readonly NativeMethods.SUBCLASSPROC _subclassProc;

    // 最小化结束事件钩子：窗口从最小化恢复时重新断言置顶（防止 DWM 抽风掉置顶）
    private readonly NativeMethods.WinEventProc _winEventProc;
    private IntPtr _winEventHook;

    private readonly ObservableCollection<MemeViewModel> _memeList = new();
    private readonly ObservableCollection<CategoryViewModel> _categoryList = new();

    // 列表构建/维护策略：复用(ReuseStrategy) 或 重建(RebuildStrategy)。
    // 按配置“启用控件复用策略”在两者间切换，切换立即生效于下一次刷新。
    // 构造函数内会立即按配置初始化；此处给默认实例以满足非空字段。
    private IMemeListStrategy _listStrategy = new RebuildStrategy();

    private string _currentCategory = string.Empty;
    private bool _editMode;

    // 拖拽重排锚点：本次拖起的那一张（e.Items[0]）的文件名。
    // 仅复用策略(ReuseStrategy.ComputeDragOrder)使用，用于把“拖起项”对齐到
    // 鼠标落点，而非 WinUI 默认的组尾对齐；重建策略忽略此值。
    private string? _dragAnchorFileName;

    // 当前置顶状态（会话内有效，启动默认置顶，不持久化到 config）
    private bool _topMost = true;

    // 内部拖拽移动：当前拖拽的 meme 模型列表（非空即表示内部拖拽，区别于外部导入）
    private List<MemeModel>? _draggingMemes;

    // 记录本窗口激活前的前台窗口（通常是正在聊天的目标应用），用于粘贴时回投 Ctrl+V
    private IntPtr _prevActiveHwnd;
    private IntPtr _lastExternalFg;
    private bool _isActive;
    private DispatcherTimer? _fgTimer;

    // 多选模式：Shift 连续选择的锚点（在 _memeList 中的索引）
    private int _lastShiftAnchor = -1;

    // 防止粘贴导入的分类对话框重入
    private bool _pasteDialogOpen;

    // 文件选择器（FolderPicker/FileOpenPicker）打开期间：屏蔽悬停预览浮窗，
    // 避免对话框抢焦点后背后图片误弹浮窗。
    public bool IsFilePickerOpen { get; internal set; }

    // 悬停放大预览：延迟定时器 + 当前待显示项
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private MemeViewModel? _pendingPreviewVm;
    private FrameworkElement? _pendingPreviewAnchor;

    // 从配置应用悬停预览触发延时（配置缺失时用默认 400ms）
    public void ApplyPreviewDelayFromConfig()
    {
        try
        {
            var cfg = App.DataEngine.Config;
            int ms = cfg?.PreviewDelayMs > 0 ? cfg.PreviewDelayMs : 400;
            _previewTimer.Interval = TimeSpan.FromMilliseconds(ms);
        }
        catch { }
    }

    // 是否允许真正关闭窗口（仅托盘“退出”时置 true；普通点 X 只隐藏）
    private bool _allowClose;

    // 窗口正在关闭/销毁中：所有异步回调(XAML 操作)据此放弃触碰控件，
    // 避免 WinUI 在视觉树销毁后仍被访问导致 native AV(0xc0000005)。
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();

        // 按配置选择列表构建策略（复用 / 重建）。切换在设置页保存后即时应用。
        ApplyListStrategyFromConfig();

        _hWnd = WindowNative.GetWindowHandle(this);

        _previewTimer.Tick += PreviewTimer_Tick;
        ApplyPreviewDelayFromConfig();

        // 置顶开关：启动默认置顶（会话内可手动关闭，不持久化）
        TopMostToggle.IsChecked = _topMost;


        SetTaskbarIcon();

        int exStyle = NativeMethods.GetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE);
        // 启动默认置顶：始终加 TOPMOST 扩展样式（用户可在会话内手动关闭）
        NativeMethods.SetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOPMOST);

        RegisterConfiguredHotKey();

        _subclassProc = NewWindowProc;
        NativeMethods.SetWindowSubclass(_hWnd, _subclassProc, SUBCLASS_ID, IntPtr.Zero);

        // 挂钩 EVENT_SYSTEM_MINIMIZEEND：本进程窗口最小化结束后重新断言置顶，
        // 弥补 WM_SYSCOMMAND 覆盖不到的真实最小化-恢复路径（参考 PowerToys）。
        _winEventProc = new NativeMethods.WinEventProc(WinEventCallback);
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero,
            _winEventProc,
            NativeMethods.GetCurrentProcessId(),
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        // 让窗口在 Win32 层面也能接收拖入的文件（QQ 等来源可能只发文件，不走 XAML DataPackage）
        NativeMethods.DragAcceptFiles(_hWnd, true);

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        RestoreWindowSize();

        if (_appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlappedPresenter)
        {
            // 启动默认置顶
            overlappedPresenter.IsAlwaysOnTop = true;
        }

        CategoryList.ItemsSource = _categoryList;
        MemeGridView.ItemsSource = _memeList;

        // 粘贴图片进窗口：改为仅在本窗口激活时由 Ctrl+V 触发（见 Root_KeyDown），
        // 不再监听剪贴板变化，避免截图等写入剪贴板时误触发“粘贴到分类”。

        Closed += Window_Closed;

        SettingsFlyout.Closed += SettingsFlyout_Closed;

        // 选中项变化经 SelectionChanged 自动启用/禁用批量操作按钮

        // 轮询前台窗口的定时器：用于把 Ctrl+V 投回用户正在用的外部窗口(如 QQ)。
        // 只在窗口可见时运行（见 SetMemeViewVisible）；窗口隐藏(后台常驻)时停止，
        // 既保证粘贴目标正确，又让后台零轮询、零 CPU 占用。
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

        // Esc 退出多选模式 / Enter 完成多选模式
        if (this.Content is FrameworkElement root)
        {
            root.KeyDown += Root_KeyDown;
            root.Loaded += (_, _) => { if (this.Content is FrameworkElement r) r.Focus(FocusState.Programmatic); };
        }

        LoadCategories();
    }

    // ---------- 窗口尺寸持久化 ----------

    // 启动还原：读 config.json 中上次保存的宽高；无有效值则用默认。最大化状态不持久化。
    private void RestoreWindowSize()
    {
        if (_appWindow == null) return;
        var cfg = App.DataEngine.Config;

        int w = (int)Math.Max(400, cfg.WindowWidth);
        int h = (int)Math.Max(300, cfg.WindowHeight);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        Log($"[窗口] 还原尺寸 {w}x{h} (预设={cfg.WindowSizePreset})");
    }

    // 退出/关闭前保存：记录当前尺寸到 config.json。最大化状态下不记录
    // （最大化置顶窗口会挡住托盘右键菜单，且还原时尺寸无意义），仅打日志跳过。
    private void SaveWindowSize()
    {
        if (_appWindow == null) return;
        var cfg = App.DataEngine.Config;

        bool maximized = _appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op && op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        if (maximized)
        {
            Log("[窗口] 当前为最大化，跳过尺寸记录");
            return;
        }

        var bounds = _appWindow.Size;
        cfg.WindowWidth = bounds.Width;
        cfg.WindowHeight = bounds.Height;
        cfg.WindowMaximized = false;
        cfg.WindowSizePreset = ClassifySize(bounds.Width, bounds.Height);
        Log($"[窗口] 保存尺寸 {bounds.Width}x{bounds.Height} (预设={cfg.WindowSizePreset})");

        _ = App.DataEngine.SaveConfigAsync();
    }

    // 依据宽高映射到最接近的尺寸预设档位（仅用于日志/调试展示）
    private static WindowSizePreset ClassifySize(int w, int h)
    {
        return (w, h) switch
        {
            (<= 800, <= 620) => WindowSizePreset.Small,
            (>= 1150, >= 880) => WindowSizePreset.Large,
            _ => WindowSizePreset.Medium
        };
    }

    // ---------- 分类 ----------

    // 供设置页在“浏览”修改存放路径后即时刷新主窗口（分类/表情）
    public void ReloadData()
    {
        LoadCategories();
    }

    // 按配置创建对应的列表策略实例。
    private static IMemeListStrategy CreateStrategy(bool reuse) =>
        reuse ? new ReuseStrategy() : new RebuildStrategy();

    // 从配置读取并应用列表策略；首次启动与“设置”保存后均会调用。
    // 复用模式切换会打日志，便于观察内存/行为变化。
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
        // 若没有任何分类文件夹，默认创建一个 "Default"
        if (App.DataEngine.GetCategories().Count == 0)
        {
            App.DataEngine.EnsureDefaultCategory();
        }

        // 用当前策略同步分类列表（复用=增量复用容器，重建=整体重建）。
        // 具体算法封装在 IMemeListStrategy.SyncCategories 内。
        _listStrategy.SyncCategories(
            _categoryList,
            App.DataEngine.GetCategories(),
            cat => App.DataEngine.GetMemes(cat).Count);

        // 默认选中上次或第一项。
        // 注意：重建模式下 SyncCategories 会整体 Clear+新建 VM 并销毁旧容器，选中视觉（蓝条/高亮）
        // 随之丢失，因此必须无条件重新赋值 SelectedItem 才能恢复高亮；仅当 target 与当前选中同名时
        // 跳过，避免无谓触发 SelectionChanged（复用模式下这一支基本不会命中，因为容器未重建）。
        var last = App.DataEngine.Config.LastCategory;
        var target = _categoryList.FirstOrDefault(c => c.Name == last) ?? _categoryList.FirstOrDefault();
        if (target != null && !target.Name.Equals(_currentCategory, StringComparison.OrdinalIgnoreCase))
        {
            CategoryList.SelectedItem = target;
            _currentCategory = target.Name;
        }
        else if (target != null)
        {
            // 重建模式下分类名没变但容器已重建：重新设回同一项以恢复选中视觉。
            CategoryList.SelectedItem = target;
        }

        RefreshMemes();
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is CategoryViewModel cat)
        {
            // 分类未变（如重复选中同一项）则跳过整段重建，避免无谓分配
            if (cat.Name.Equals(_currentCategory, StringComparison.OrdinalIgnoreCase))
                return;
            _currentCategory = cat.Name;
            _ = App.DataEngine.UpdateConfigAsync(c => c.LastCategory = cat.Name);
            RefreshMemes();
        }
    }

    // 当前右键所操作的分类（由 ContextFlyout.Opening 写入，供各 Click 使用）
    private CategoryViewModel? _contextCategory;

    // 右键分类项：记录当前分类，供 删除/重命名 使用
    private void CategoryItemContextFlyout_Opening(object? sender, object e)
    {
        if (sender is MenuFlyout flyout && flyout.Target is FrameworkElement fe)
            _contextCategory = fe.DataContext as CategoryViewModel;
        if (_contextCategory != null)
            Log($"右键分类项: {_contextCategory.Name}");
    }

    // 在文件资源管理器中打开该分类对应的文件夹
    private void CategoryOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_contextCategory == null) return;
        var dir = System.IO.Path.Combine(App.DataEngine.BaseDir, _contextCategory.Name);
        Utils.OpenInExplorer(dir, select: false, logTag: "打开分类文件夹");
    }

    private async void CategoryNew_Click(object sender, RoutedEventArgs e)
        => await ShowAddCategoryDialog();

    private async void CategoryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_contextCategory != null)
            await DeleteCategoryConfirmed(_contextCategory);
    }

    private async void CategoryRename_Click(object sender, RoutedEventArgs e)
    {
        if (_contextCategory != null)
            await ShowRenameCategoryDialog(_contextCategory);
    }

    // 重命名分类对话框：同步重命名物理文件夹并刷新分类列表
    private async Task ShowRenameCategoryDialog(CategoryViewModel cat)
    {
        var input = new TextBox
        {
            Text = cat.Name,
            PlaceholderText = "输入新的分类名称",
        };
        var dialog = new ContentDialog
        {
            Title = "重命名分类",
            Content = input,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            XamlRoot = this.Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = input.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName)) return;

        bool ok = await App.DataEngine.RenameCategoryAsync(cat.Name, newName);
        if (!ok)
        {
            try
            {
                var err = new ContentDialog
                {
                    Title = "重命名失败",
                    Content = "分类重命名失败（可能名称已存在或文件夹无法访问）。",
                    CloseButtonText = "确定",
                    XamlRoot = this.Content.XamlRoot
                };
                await err.ShowAsync();
            }
            catch (Exception ex) { Logger.Log($"[分类重命名] 错误弹窗失败: {ex.Message}"); }
            return;
        }

        Log($"重命名分类「{cat.Name}」-> 「{newName}」");
        // 重建分类列表（x:Bind 默认 OneTime，需重建以刷新分类名显示），
        // LoadCategories 内部会按 LastCategory 恢复当前分类并 RefreshMemes
        LoadCategories();
    }

    // 删除分类（含确认对话框与 UI 更新）
    private async Task DeleteCategoryConfirmed(CategoryViewModel cat)
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
        if (!ok) return;

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

    // 拖拽图片到分类列表：仅接受内部移动，并高亮可放置
    private void CategoryList_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingMemes != null && _draggingMemes.Count > 0)
        {
            // 拖入表情图片：仅关闭 CanReorderItems 的插入占位，避免分类列表被撑开；
            // 不关闭 CanDragItems，以保证分类项仍能作为 drop 目标接收图片（移动到该分类）。
            CategoryList.CanReorderItems = false;
            // 与 DragItemsStarting 的 RequestedOperation=Move 保持一致，否则 WinUI 认为不兼容显示禁止符号
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            e.DragUIOverride.Caption = "移动到该分类";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            // 分类自身重排序：开启 CanReorderItems，允许占位撑开动画
            CategoryList.CanReorderItems = true;
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        }
    }

    // 分类列表内部拖拽重排完成：WinUI 已把 _categoryList 排好，读顺序写回 .metadata.json
    private async void CategoryList_DragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
    {
        // 重排结束，恢复 CanReorderItems（image 拖入时曾被临时关闭）
        CategoryList.CanReorderItems = true;
        if (e.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move &&
            e.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy)
            return;

        var ordered = _categoryList.Select(c => c.Name).ToList();
        await App.DataEngine.ReorderCategoriesAsync(ordered);
        Log($"分类重排写回 {ordered.Count} 个分类顺序");
    }

    private async void CategoryListItem_Drop(object sender, DragEventArgs e)
    {
        // 优先用 DragItemsStarting 记录的 _draggingMemes；
        // 若它已被 DragItemsCompleted 提前清空（跨控件拖拽事件顺序不确定），
        // 则从 e.DataView 的 StorageItems 还原被拖项，避免依赖共享字段。
        List<MemeModel> memes;
        if (_draggingMemes != null && _draggingMemes.Count > 0)
        {
            memes = _draggingMemes;
            _draggingMemes = null;
        }
        else
        {
            memes = await MemesFromDataViewAsync(e.DataView);
            if (memes.Count == 0)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
                return;
            }
        }

        // 目标分类 = 被拖放到的那个分类项（sender 即该项模板根 Grid，DataContext 为分类）
        var targetCat = (sender as FrameworkElement)?.DataContext as CategoryViewModel;
        Log($"[分类Drop] 触发, 目标分类={targetCat?.Name ?? "(无)"}, 项数={memes.Count}");
        if (targetCat == null) return;

        int moved = memes.Count(m => !m.Category.Equals(targetCat.Name, StringComparison.OrdinalIgnoreCase));
        if (moved > 0)
        {
            if (!await GuardMoveConflictAsync(memes, targetCat.Name))
                return;
            await App.DataEngine.MoveMemesToCategoryAsync(memes, targetCat.Name);
            Log($"Drop: 内部移动 {moved} 张图片到分类「{targetCat.Name}」");
            // 场景B：内容减少但顺序不变。精准移除被移走的项，保持滚动条位置。
            RemoveFromCurrentView(memes);
            UpdateCategoryCounts();
        }
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    // 控制前台窗口轮询定时器的启停（与数据刷新解耦）：
    // - 可见时轮询前台窗口，保证粘贴目标正确；
    // - 隐藏(后台常驻)时停止，零 CPU 后台。
    private void SetMemeViewVisible(bool visible)
    {
        if (visible)
        {
            if (MemeGridView.ItemsSource != _memeList)
            {
                CategoryList.ItemsSource = _categoryList;
                MemeGridView.ItemsSource = _memeList;
            }
            // 隐藏时 CategoryList.ItemsSource 被置空导致选中容器销毁、蓝条/高亮丢失；
            // 重新绑回后必须重新断言选中，待容器生成后再设回以恢复视觉。
            var sel = _categoryList.FirstOrDefault(c => c.Name == _currentCategory)
                      ?? _categoryList.FirstOrDefault();
            if (sel != null)
            {
                CategoryList.SelectedItem = null;
                DispatcherQueue.TryEnqueue(() => { CategoryList.SelectedItem = sel; });
            }
            _fgTimer?.Start();
        }
        else
        {
            _fgTimer?.Stop();
            // 隐藏时仅断开 ItemsSource，让 GridView 的 Item 容器（含 Image 控件）
            // 从可视化树移除，WinUI 框架会在下一帧自动释放其 GPU 纹理；
            // 再强制 GC 回收非托管资源。两种列表策略共用此极简逻辑，不额外摘树。
            MemeGridView.ItemsSource = null;
            CategoryList.ItemsSource = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // 仅复用模式下打印内存诊断（重建模式无需关注 VM 常驻情况）。
            if (App.DataEngine.Config.UseControlReuse)
            {
                Log($"[内存诊断] 隐藏释放(复用模式): _memeList={_memeList.Count} _categoryList={_categoryList.Count} " +
                    $"VM存活BitmapImage={MemeViewModel.LiveBitmapImageCount} " +
                    $"托管堆={GC.GetTotalMemory(false) / 1024}KB GC代数0/1/2={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
            }
            HidePreviewPopup(true, "SetMemeViewVisible");
        }
    }

    private void HidePreviewPopup(bool immediate = false, string reason = "")
    {
        _previewTimer.Stop();
        _pendingPreviewVm = null;
        _pendingPreviewAnchor = null;

        if (!PreviewPopup.IsOpen)
        {
            _previewFadingOut = false;
            return;
        }

        // 窗口隐藏/销毁等场景直接关闭，不做淡出
        if (immediate || _isClosing)
        {
            PreviewPopup.IsOpen = false;
            // 断开预览图源：Popup 子树一直存活于可视化树，不清空会导致
            // 高分辨率预览纹理常驻（与列表重建模式无关的独立泄漏路径）。
            PreviewImage.Source = null;
            _previewFadingOut = false;
            _suppressNextMove = false;
            Log($"[预览] 浮窗已关闭 (来源=immediate{reason})");
            return;
        }

        // 已在淡出则忽略重复请求
        if (_previewFadingOut) return;
        _previewFadingOut = true;

        if (PreviewBorder != null)
        {
            var sb = new Storyboard();
            var da = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(PreviewFadeOutMs)
            };
            Storyboard.SetTarget(da, PreviewBorder);
            Storyboard.SetTargetProperty(da, "Opacity");
            sb.Completed += (_, _) =>
            {
                // 淡出期间若又被重新显示（_previewFadingOut 被置 false），则不关闭
                if (_previewFadingOut)
                {
                    _previewFadingOut = false;
                    PreviewPopup.IsOpen = false;
                    PreviewImage.Source = null;
                    Log($"[预览] 浮窗已关闭 (来源=fadeout{reason})");
                }
            };
            sb.Children.Add(da);
            sb.Begin();
        }
        else
        {
            PreviewPopup.IsOpen = false;
            PreviewImage.Source = null;
            _previewFadingOut = false;
            Log($"[预览] 浮窗已关闭 (来源=direct{reason})");
        }
    }

    // 从 DataView 的 StorageItems（拖拽时写入的文件路径）还原被拖的 MemeModel 列表
    private async Task<List<MemeModel>> MemesFromDataViewAsync(DataPackageView view)
    {
        var result = new List<MemeModel>();
        if (view == null || !view.Contains(StandardDataFormats.StorageItems)) return result;
        try
        {
            var items = await view.GetStorageItemsAsync();
            var all = App.DataEngine.GetAllMemes();
            foreach (var item in items)
            {
                var name = System.IO.Path.GetFileName(item.Path);
                var m = all.FirstOrDefault(x => x.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (m != null) result.Add(m);
            }
        }
        catch (Exception ex)
        {
            Log("MemesFromDataViewAsync 失败: " + ex.Message);
        }
        return result;
    }

    private void CategoryListItem_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingMemes != null && _draggingMemes.Count > 0)
        {
            // 与 DragItemsStarting 的 RequestedOperation=Move 保持一致
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
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
        await ShowAddCategoryDialog();
    }

    // 新增分类对话框：成功后在列表末尾追加并选中
    private async Task ShowAddCategoryDialog()
    {
        var box = new TextBox { PlaceholderText = "输入新分类名称" };
        var dialog = new ContentDialog
        {
            Title = "新增分类",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            XamlRoot = this.Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
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
        var keyword = SearchBox.Text?.Trim();
        var memes = App.DataEngine.GetMemes(
            string.IsNullOrWhiteSpace(_currentCategory) ? null : _currentCategory,
            string.IsNullOrWhiteSpace(keyword) ? null : keyword);

        // 用当前策略刷新表情列表（复用=增量复用 VM，重建=整体 Clear+重建）。
        _listStrategy.RefreshMemes(_memeList, memes);

        // 复用语义下记录增量统计，便于诊断（重建模式为全量重建，无增量可记）。
        if (_listStrategy is ReuseStrategy)
        {
            int newCount = memes.Count;
            int oldCount = _memeList.Count;
            Log($"[诊断] RefreshMemes VM数={_memeList.Count} 新项数={newCount}");
        }

        UpdateCategoryCounts();

        // 编辑模式下列表重建(如搜索/刷新)后，重新显示复选框并把原生选中态镜像回新 VM
        if (_editMode)
        {
            SetSelectionBoxVisible(true);
            SyncSelectionToViewModels();
        }
    }

    // 新导入的表情包优先级最高(DateAdded 最新)，按现有排序规则(Priority 降序、
    // 同值 DateAdded 降序)应排在列表最前。直接在头部插入，避免整列表重建。
    private void InsertMemeAtFront(MemeViewModel vm)
    {
        _memeList.Insert(0, vm);
    }

    // 精准从当前视图移除若干项（不 Clear 重建，保持滚动条位置与选中状态）。
    // 用于“移动到其他分类”等“内容减少但顺序不变”的场景。
    private void RemoveFromCurrentView(IEnumerable<MemeModel> removed)
    {
        var names = new HashSet<string>(
            removed.Select(m => m.FileName), StringComparer.OrdinalIgnoreCase);
        for (int i = _memeList.Count - 1; i >= 0; i--)
            if (names.Contains(_memeList[i].FileName))
                _memeList.RemoveAt(i);
    }

    // 重排后顺序已由 WinUI / _memeList 排好，仅需把内存模型 Priority 同步到 ViewModel 顺序，
    // 不重建集合（保持滚动条位置）。这里直接信任 _memeList 当前顺序即最终顺序。
    private void SyncCurrentViewAfterReorder()
    {
        // 无需操作：_memeList 已是正确顺序，ViewModel 实例保持不变。
    }

    private void UpdateCategoryCounts()
    {
        // 直接基于内存缓存计数，避免每个分类都走一次 GetMemes().ToList() 的临时分配
        var cache = App.DataEngine.GetAllMemes();
        foreach (var c in _categoryList)
            c.Count = cache.Count(m => m.Category.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- 悬停放大预览（Popup）----------

    // 淡入/淡出动画时长（毫秒），调快就改小这两个值
    private const int PreviewFadeInMs = 95;
    private const int PreviewFadeOutMs = 0;

    // 浮窗是否正在淡出中（淡出动画结束前不真正关闭，便于快速划过时复用）
    private bool _previewFadingOut;

    // 上次鼠标在窗口内的位置（DIP）。用于过滤“静止时 WinUI 仍产生的 PointerMoved 抖动”，
    // 只有鼠标真正移动超过阈值才关闭预览（避免鼠标没动却反复开关）。
    private Windows.Foundation.Point _lastPointerPos;
    private const double PreviewMoveThreshold = 3;
    // 浮窗刚打开时紧跟的一次 PointerMoved（同位置抖动）忽略，避免误关
    private bool _suppressNextMove;

    private void MemeItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // 编辑模式或窗口隐藏时不显示预览
        // 正在拖拽（内部拖出/重排）时不显示预览：避免遮挡鼠标，并杜绝拖拽会话
        // 与预览浮窗异步回调在 native 层交错访问可视化树。
        if (_editMode || !_isVisible || _isClosing || _draggingMemes != null) return;
        // 文件选择器打开期间不弹预览浮窗（避免对话框抢焦点后误触发）
        if (IsFilePickerOpen) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not MemeViewModel vm) return;

        _lastPointerPos = e.GetCurrentPoint((UIElement)this.Content).Position;

        // 若浮窗已开（且未在淡出），直接切换内容/位置并淡入，无需再等延时
        if (PreviewPopup.IsOpen && !_previewFadingOut)
        {
            ShowPreviewPopup(vm, fe);
        }
        else
        {
            _pendingPreviewVm = vm;
            _pendingPreviewAnchor = fe;
            _previewTimer.Start();
        }
    }

    private void MemeItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _previewTimer.Stop();
        _pendingPreviewVm = null;
        _pendingPreviewAnchor = null;
        // 鼠标离开表情项即关闭预览（移动即取消，不依赖命中测试）
        HidePreviewPopup(reason: "PointerExited");
    }

    // 鼠标在窗口内移动：预览只是临时提示，鼠标真正移动（超过阈值）即取消。
    // 用距离阈值过滤“静止时 WinUI 仍会派发的 PointerMoved 抖动”，避免没动却开关。
    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!PreviewPopup.IsOpen) return;

        var pt = e.GetCurrentPoint((UIElement)this.Content).Position;
        double dx = pt.X - _lastPointerPos.X;
        double dy = pt.Y - _lastPointerPos.Y;
        _lastPointerPos = pt;

        // 忽略浮窗刚打开后那次同位置抖动
        if (_suppressNextMove)
        {
            _suppressNextMove = false;
            Log($"[预览] PointerMoved 忽略(打开后抖动) pos=({pt.X:F0},{pt.Y:F0})");
            return;
        }

        if (dx * dx + dy * dy > PreviewMoveThreshold * PreviewMoveThreshold)
        {
            Log($"[预览] PointerMoved 关闭 (dx={dx:F1}, dy={dy:F1}) pos=({pt.X:F0},{pt.Y:F0})");
            HidePreviewPopup(reason: "PointerMoved");
        }
    }

    private void PreviewTimer_Tick(object? sender, object e)
    {
        Log($"[预览] TimerTick -> Show (pending={_pendingPreviewVm?.Title})");
        _previewTimer.Stop();
        if (_pendingPreviewVm == null || _pendingPreviewAnchor == null) return;
        if (_isClosing || !_isVisible) return;

        ShowPreviewPopup(_pendingPreviewVm, _pendingPreviewAnchor);
    }

    private void ShowPreviewPopup(MemeViewModel vm, FrameworkElement anchor)
    {
        PreviewTitle.Text = vm.Title;

        // 先把 Image 源设好，待布局完成后量取尺寸再定位
        PreviewImage.Source = vm.PreviewSource;

        // 锚点矩形（窗口坐标 DIP）
        var anchorRect = GetElementWindowRect(anchor);

        // 先打开以便 Measure 出内容真实尺寸
        PreviewPopup.IsOpen = true;

        // 标记：忽略浮窗刚打开后紧跟的一次 PointerMoved（同位置抖动），
        // 该次移动不做关闭判定，避免“鼠标没动却立即关闭”。
        _suppressNextMove = true;

        if (PreviewPopup.Child is FrameworkElement child)
        {
            child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double pw = child.DesiredSize.Width;
            double ph = child.DesiredSize.Height;

            var workArea = GetWindowWorkArea();
            var (x, y, placement) = Utils.PlacePopup(
                anchorRect, pw, ph, workArea, Placement.Above);

            PreviewPopup.HorizontalOffset = x;
            PreviewPopup.VerticalOffset = y;

            var (nw, nh) = vm.GetPreviewNaturalSize();
            var (ow, oh) = vm.GetPreviewOutputSize();
            Log($"[预览] 显示: 标题={vm.Title} | 原图={nw}x{nh} | 实际输出图={ow}x{oh} | " +
                $"浮窗={pw:F0}x{ph:F0} | 坐标=({x:F0},{y:F0}) | 方位={placement}");
        }

        // 取消可能正在进行的淡出并重新淡入（快速划过多个表情时复用同一浮窗）
        FadeInPreview();
    }

    // 浮窗淡入：取消淡出状态，从 0→1 渐显
    private void FadeInPreview()
    {
        _previewFadingOut = false;
        if (PreviewBorder == null) return;

        var sb = new Storyboard();
        var da = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(PreviewFadeInMs)
        };
        Storyboard.SetTarget(da, PreviewBorder);
        Storyboard.SetTargetProperty(da, "Opacity");
        sb.Children.Add(da);
        sb.Begin();
    }

    // 取得元素相对【窗口内容根】的矩形（DIP）。Popup 的 Offset 是窗口相对坐标，
    // 因此定位与命中测试必须统一用窗口坐标，不能用屏幕坐标。
    private Windows.Foundation.Rect GetElementWindowRect(FrameworkElement element)
    {
        var root = (FrameworkElement)this.Content;
        var transform = element.TransformToVisual(root);
        var topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        return new Windows.Foundation.Rect(topLeft.X, topLeft.Y, element.ActualWidth, element.ActualHeight);
    }

    // 取得主窗口所在屏幕的工作区，转换为【窗口坐标(DIP)】，
    // 用于把浮窗限制在屏幕内（而非限制在窗口内——浮窗可越过主窗口边界，只要不超出屏幕）。
    private Windows.Foundation.Rect GetWindowWorkArea()
    {
        // 默认兜底：相对窗口的“无限”区域（即不限制），避免窗口位置取不到时把浮窗夹死。
        var fallback = new Windows.Foundation.Rect(
            -this.Bounds.X, -this.Bounds.Y,
            double.PositiveInfinity, double.PositiveInfinity);
        try
        {
            var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd),
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            if (display != null && _appWindow != null)
            {
                var r = display.WorkArea;          // 屏幕坐标
                var pos = _appWindow.Position;      // 窗口左上角屏幕坐标
                return new Windows.Foundation.Rect(
                    r.X - pos.X, r.Y - pos.Y, r.Width, r.Height);
            }
        }
        catch { }
        return fallback;
    }


    // 根据当前是否有选中项，启用/禁用批量操作按钮（无选中时灰掉且不可点）
    private void UpdateBatchButtons()
    {
        bool anySelected = MemeGridView.SelectedItems.Count > 0;
        if (BatchExportButton != null) BatchExportButton.IsEnabled = anySelected;
        if (BatchMoveButton != null) BatchMoveButton.IsEnabled = anySelected;
        if (DeleteButton != null) DeleteButton.IsEnabled = anySelected;
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
            // 背景/前景的蓝色由 XAML 写死常亮，这里不再处理颜色，仅切换文字与模式
            BatchBar.Visibility = Visibility.Visible;
            // 编辑模式开启内置重排：落点由 WinUI 自己算准
            MemeGridView.CanReorderItems = true;
            // 多选模式由配置决定：
            //  - false：资源管理器风格 ListViewSelectionMode.Multiple（系统自带复选框），隐藏自绘复选框
            //  - true ：ListViewSelectionMode.Extended + 自绘右上角复选框，支持 shift 连续/反选
            bool explorerStyle = App.DataEngine.Config.ExplorerStyleMultiSelect;
            MemeGridView.SelectionMode = explorerStyle
                ? ListViewSelectionMode.Extended
                : ListViewSelectionMode.Multiple;
            // 仅 Extended 模式显示我们自绘的复选框
            SetSelectionBoxVisible(explorerStyle);
        }
    }

    // ---------- 点击表情（非修改模式 = 粘贴；多选模式 = 切换选中） ----------
    // 用 Tapped 而非 PointerPressed：拖拽(CanDrag)会取消 Tapped，避免“先单击粘贴一次、再拖出又粘贴一次”

    private async void MemeItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        HidePreviewPopup(reason: "Tapped");

        if (sender is not FrameworkElement fe || fe.DataContext is not MemeViewModel clicked)
        {
            Log("MemeItem_Tapped: 取不到 MemeViewModel, sender=" + sender?.GetType().Name);
            return;
        }

        int index = _memeList.IndexOf(clicked);

        // ---- 编辑模式：选中完全交给 GridView 原生(SelectionMode=Multiple)处理，
        //      Tapped 不再手动切换，避免与控件自带选中逻辑冲突导致要双击。
        if (_editMode)
        {
            _lastShiftAnchor = index;
            return;
        }

        // ---- 普通模式：发送图片 ----
        // 目标窗口优先级：轮询记录的外部前台窗口(_lastExternalFg) >
        // 失去激活时记录的上一个外部窗口(_prevActiveHwnd) > 实时前台窗口(liveFg)。
        // _lastExternalFg 由 _fgTimer 在窗口可见且未激活时持续刷新。
        // liveFg 取“点击瞬间”的前台窗口：Tapped 事件通常先于窗口激活完成触发，
        // 此时前台往往仍是用户正在用的外部输入框(QQ 等)，故可作兜底。
        IntPtr liveFg = NativeMethods.GetForegroundWindow();
        IntPtr target = IntPtr.Zero;
        if (_lastExternalFg != IntPtr.Zero && _lastExternalFg != _hWnd)
            target = _lastExternalFg;
        else if (_prevActiveHwnd != IntPtr.Zero && _prevActiveHwnd != _hWnd)
            target = _prevActiveHwnd;
        else if (liveFg != IntPtr.Zero && liveFg != _hWnd)
            target = liveFg;

        // 兜底：若仍解析不到有效外部窗口（target 为空或就是自己窗口），
        // 不执行粘贴，避免把图片投回自身窗口触发“粘贴到分类”弹窗。
        if (target == IntPtr.Zero || target == _hWnd)
        {
            Log($"单击(发送模式): 未解析到有效外部窗口(target={target})，取消本次粘贴");
            return;
        }

        Log($"单击(发送模式): 发送图片 {clicked.Title} 到前台窗口 target={target}");
        await PasteService.OutputMemeToCursorAsync(clicked.LocalPath, target);
        await App.DataEngine.IncrementUsageAsync(clicked.Hash);
    }

    // 容器(项)为数据生成时：若处于编辑模式且为 Extended 风格，立即把复选框设为可见，
    // 解决虚拟化下滚动后新出现的 item 默认 Collapsed 的问题。
    // Explorer 风格(Multiple)下不显示自绘复选框（用系统自带）。
    private void MemeGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (_editMode && args.Phase == 0 && args.ItemContainer != null)
        {
            var box = FindCheckBox(args.ItemContainer);
            if (box != null)
                box.Visibility = App.DataEngine.Config.ExplorerStyleMultiSelect
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    // GridView 原生选中变化 → 更新批量操作按钮可用状态，并镜像到 VM(驱动右上角复选框)
    private void MemeGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncSelectionToViewModels();
        UpdateBatchButtons();
    }

    // 把 GridView 原生选中态单向镜像到各 MemeViewModel.IsSelected，
    // 供 ItemTemplate 里的 CheckBox 显示勾选(纯指示，不反向写回)。
    // 先整体清一遍再按 SelectedItems 置位，覆盖“反选/全清”等场景。
    private void SyncSelectionToViewModels()
    {
        if (_isClosing) return;
        var selected = new HashSet<MemeViewModel>(
            MemeGridView.SelectedItems.Cast<MemeViewModel>());
        foreach (var vm in _memeList)
            vm.IsSelected = selected.Contains(vm);
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
        // 拖拽会话开始即彻底关闭预览浮窗：避免浮窗淡出 Storyboard 异步回调
        // 与 GridView 拖拽重排会话在 native 层交错访问同一容器树导致 failfast；
        // 同时拖拽时不再弹浮窗，避免遮挡鼠标视野。
        HidePreviewPopup(immediate: true, "拖拽开始");
        // 停止预览定时器：dump 显示崩溃发生在 DispatcherTimer Tick 的
        // UIAffinityReleaseQueue::DoCleanup 里销毁 DragItemsStartingEventArgs 时，
        // 与渲染 Tick 重入撞车触发 framework 层 reentrancy failfast。
        // 拖拽期间停掉定时器，消除这个竞态窗口。
        _previewTimer.Stop();

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
        // 锚点 = 实际拖起的那一张（e.Items[0]），用于重排时让它对齐鼠标落点。
        _dragAnchorFileName = draggedVms.Count > 0 ? draggedVms[0].FileName : null;
        Log($"DragItemsStarting: 拖出 {_draggingMemes.Count} 张图片 (首项 {group[0].Title}, 锚点={_dragAnchorFileName})");

        // 设文件以便拖到外部时复制
        try
        {
            var files = group
                .Select(v => StorageFile.GetFileFromPathAsync(v.LocalPath).AsTask().Result)
                .ToArray();
            e.Data.SetStorageItems(files);
            // 拖出图片时以图片格式输出：仅单张生效（图片剪贴板格式只能承载一张位图，
            // 多张只能靠文件格式）。开启后老版本 QQ 才会把单张拖出识别为图片而非文件。
            // 硬保险：GIF 永不走 SetBitmap —— dump 证实 DragItemsStartingEventArgs 内部
            // 的 DataPackage 在延迟释放时会因 Bitmap/StorageFile 跨线程清理触发 framework
            // 层 reentrancy failfast(0xc000027b)，GIF 本就靠文件路径被 QQ 识别，无需 Bitmap。
            bool isGif = files.Length == 1 &&
                string.Equals(Path.GetExtension(files[0].Path), ".gif", StringComparison.OrdinalIgnoreCase);
            if (App.DataEngine.Config.DragOutputAsImage && files.Length == 1 && !isGif)
            {
                e.Data.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(files[0]));
            }
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

        // 拖拽结束：恢复预览定时器（仅当窗口可见）。与 DragItemsStarting 里的
        // _previewTimer.Stop() 成对，避免拖拽期间停定时器导致预览功能永久失效。
        if (_isVisible && !_isClosing)
            _previewTimer.Start();

        // 记录整组被拖项（编辑模式多选拖拽时是整组），重排后据此恢复多选状态
        var draggedGroup = _draggingMemes?.ToList() ?? new List<MemeModel>();

        // 用当前策略计算写回顺序：复用策略做“锚点对齐”，重建策略沿用 WinUI 默认顺序。
        var orderedFileNames = _listStrategy.ComputeDragOrder(_memeList, draggedGroup, _dragAnchorFileName)
            ?? _memeList.Select(m => m.FileName).ToList();

        Log($"DragItemsCompleted: 重排完成, 项数={orderedFileNames.Count}");

        var ordered = orderedFileNames;

        try
        {
            await App.DataEngine.ReorderMemesAsync(_currentCategory, ordered);
            Log($"DragItemsCompleted: 重排写回 {ordered.Count} 张图片到分类「{_currentCategory}」");
        }
        catch (Exception ex)
        {
            Log($"[拖拽] ReorderMemesAsync 写回失败: {ex}");
        }
        // 场景A：仅顺序变、内容不变。已就地调整 _memeList，不重建集合以保持滚动条位置。

        // 编辑模式下：重排时 WinUI 会把选中重置为仅被拖动的那一张，导致多选变单选；
        // 这里按拖拽开始时记录的整组(_draggingMemes/draggedGroup)恢复多选高亮。
        if (_editMode && draggedGroup.Count > 0 && !_isClosing)
        {
            try
            {
                var vms = draggedGroup
                    .Select(m => _memeList.FirstOrDefault(v => v.FileName.Equals(m.FileName, StringComparison.OrdinalIgnoreCase)))
                    .Where(v => v != null)
                    .ToList()!;
                MemeGridView.SelectedItems.Clear();
                foreach (var vm in vms)
                    MemeGridView.SelectedItems.Add(vm);
                UpdateBatchButtons();
            }
            catch (Exception ex)
            {
                Log($"[拖拽] 恢复多选选中失败: {ex}");
            }
        }

        _draggingMemes = null;
        _dragAnchorFileName = null;
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
                if (!_isClosing && _isVisible)
                {
                    RefreshMemes();
                    UpdateCategoryCounts();
                }
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                return;
            }
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
            foreach (var item in items)
            {
                if (item is StorageFile file && IsImage(file.FileType))
                {
                    var (imported, _) = await App.DataEngine.ImportMemeAsync(file.Path, _currentCategory);
                    if (imported != null) importedCount++;
                }
            }
        }

        // 2) Bitmap（剪贴板/截图类拖拽）
        if (view.Contains(StandardDataFormats.Bitmap))
        {
            try
            {
                var streamRef = await view.GetBitmapAsync();
                using var stream = await streamRef.OpenReadAsync();
                var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"meme_{Guid.NewGuid():N}.png");
                using (var outStream = System.IO.File.Create(tempPath))
                {
                    await stream.AsStreamForRead().CopyToAsync(outStream);
                }
                var (imported, _) = await App.DataEngine.ImportMemeAsync(tempPath, _currentCategory);
                if (imported != null) importedCount++;
                try { System.IO.File.Delete(tempPath); } catch { }
            }
            catch (Exception ex) { Log("拖入(Bitmap)失败: " + ex.Message); }
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
        else
        {
            Log("拖入: 未导入任何图片（已忽略不符合要求的拖拽对象）");
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
                    Log($"拖入文件[{i}] = {path}");
                    if (System.IO.File.Exists(path) && IsImage(System.IO.Path.GetExtension(path)))
                        paths.Add(path);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buf);
                }
            }
            NativeMethods.DragFinish(hDrop);

            if (paths.Count > 0)
                Log($"拖入 {paths.Count} 个文件, 目标分类={_currentCategory}");

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
        int duplicateCount = 0;
        foreach (var path in paths)
        {
            var (imported, dup) = await App.DataEngine.ImportMemeAsync(path, _currentCategory);
            if (imported == null) continue;
            if (dup) duplicateCount++;
            else importedCount++;
        }

        // 回到 UI 线程刷新
        DispatcherQueue.TryEnqueue(() =>
        {
            // 窗口已隐藏/销毁则放弃，避免操作已不存在的 XAML 导致 native AV
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

    // ---------- 右键菜单（XAML ContextFlyout 绑定）----------

    // 当前右键所操作的表情（由 ContextFlyout.Opening 写入，供各 Click 使用）
    private MemeViewModel? _contextMeme;

    // 表情右键菜单打开时：记录当前表情，并动态填充“移动到其他分类”子菜单
    private void MemeItemContextFlyout_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout || flyout.Target is not FrameworkElement fe)
            return;
        if (fe.DataContext is not MemeViewModel vm)
            return;

        _contextMeme = vm;
        Log("右键单击表情项: " + vm.Title);

        // 动态子菜单：列出除当前分类外的所有分类
        // 注意：DataTemplate 内的 x:Name 不会提升为页面字段，这里从 flyout 里按类型/文本查找。
        var moveSub = flyout.Items
            .OfType<MenuFlyoutSubItem>()
            .FirstOrDefault(s => s.Text == "移动到其他分类");
        if (moveSub == null) return;

        moveSub.Items.Clear();
        bool hasTarget = false;
        foreach (var cat in _categoryList)
        {
            if (cat.Name.Equals(vm.Category, StringComparison.OrdinalIgnoreCase)) continue;
            hasTarget = true;
            var targetName = cat.Name;
            var moveItem = new MenuFlyoutItem { Text = cat.Name };
            moveItem.Click += async (_, __) => MoveMemeToCategory(vm, targetName);
            moveSub.Items.Add(moveItem);
        }
        moveSub.IsEnabled = hasTarget;
    }

    private async void MemeDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMeme == null) return;
        var vm = _contextMeme;
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
    }

    private void MemeOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMeme == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _contextMeme.Model.LocalPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"[打开图片] 失败: {ex.Message}");
        }
    }

    // 在文件资源管理器中定位并选中该图片（explorer /select,"路径"）
    private void MemeOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMeme == null) return;
        Utils.OpenInExplorer(_contextMeme.Model.LocalPath, select: true, logTag: "打开所在文件夹");
    }

    private async void MemeRename_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMeme == null) return;
        var vm = _contextMeme;
        var input = new TextBox
        {
            Text = vm.Title,
            PlaceholderText = "输入新的名称"
        };
        var dialog = new ContentDialog
        {
            Title = "重命名",
            Content = input,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            XamlRoot = this.Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await App.DataEngine.RenameMemeAsync(vm.Model, input.Text);
            Log($"重命名「{vm.Title}」-> 「{input.Text}」");
            // 仅更新该项的显示标题，不重建整个列表（避免滚动条重置/容器刷新）。
            // RenameMemeAsync 已写入 metadata 与内存缓存，这里只通知 UI 刷新 tooltip。
            vm.Title = input.Text;
        }
    }

    // 移动前的 hash 冲突守卫：若目标分类已存在相同图片则弹模态提示并阻止移动，
    // 避免同名(hash)文件被静默覆盖导致目标分类原有图片丢失。返回 true 表示可继续移动。
    private async Task<bool> GuardMoveConflictAsync(IEnumerable<MemeModel> memes, string targetCategory)
    {
        var conflict = await App.DataEngine.FindMoveConflictAsync(memes, targetCategory);
        if (conflict == null) return true;

        // 收集每个冲突项：源图片 + 目标分类中同 hash 的已有图片
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

        // 弹窗正文：先说明，再逐行列出冲突对 "源 -> 目标"
        var lines = new List<string>
        {
            $"分类\"{conflict}\"已经存在相同的图片",
            "",
            "冲突明细:"
        };
        foreach (var (src, dst) in conflicts)
            lines.Add($"\"{Label(src)}\" -> \"{Label(dst)}\"");

        try
        {
            var textBlock = new TextBlock
            {
                Text = string.Join("\n", lines),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            var dlg = new ContentDialog
            {
                Title = "移动图片失败",
                Content = textBlock,
                CloseButtonText = "确定",
                XamlRoot = this.Content.XamlRoot,
            };
            await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            Log($"[移动冲突] 提示窗失败: {ex.Message}");
        }
        return false;
    }

    // 移动表情到其他分类（编辑模式且有选中项则移动所有选中项，否则只移动当前项）
    private async void MoveMemeToCategory(MemeViewModel vm, string targetName)
    {
        List<MemeViewModel> toMove;
        var selected = MemeGridView.SelectedItems.Cast<MemeViewModel>().ToList();
        if (_editMode && selected.Count > 0)
            toMove = selected;
        else
            toMove = new List<MemeViewModel> { vm };

        var models = toMove.Select(m => m.Model).ToList();
        if (!await GuardMoveConflictAsync(models, targetName))
            return;

        await App.DataEngine.MoveMemesToCategoryAsync(models, targetName);
        Log($"右键移动 {toMove.Count} 张图片到分类「{targetName}」");
        // 内容减少、顺序不变：精准移除被移走的项，保持滚动条位置
        RemoveFromCurrentView(models);
        UpdateCategoryCounts();
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
            var (imported, _) = await App.DataEngine.ImportMemeAsync(file, _currentCategory);
            if (imported != null)
                InsertMemeAtFront(new MemeViewModel(imported));
        }
        UpdateCategoryCounts();
    }

    // 当前 GridView 原生选中的项
    private List<MemeViewModel> SelectedMemeViewModels()
        => MemeGridView.SelectedItems.Cast<MemeViewModel>().ToList();

    private async void BatchExportButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMemeViewModels();
        if (selected.Count == 0) return;

        var folder = await PickerHelper.PickFolderAsync(this);
        if (folder == null) return;

        await App.DataEngine.ExportMemesAsync(selected.Select(m => m.Model), folder);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMemeViewModels();
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
        RemoveFromCurrentView(selected.Select(m => m.Model));
        MemeGridView.SelectedItems.Clear();
        UpdateCategoryCounts();
    }

    // 批量移动到：弹出分类下拉，点击后将选中项移动到该分类
    private void BatchMoveFlyout_Opening(object? sender, object e)
    {
        if (BatchMoveFlyout == null) return;
        BatchMoveFlyout.Items.Clear();

        var selected = SelectedMemeViewModels();
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
                var models = selected.Select(m => m.Model).ToList();
                if (!await GuardMoveConflictAsync(models, targetName))
                    return;
                await App.DataEngine.MoveMemesToCategoryAsync(models, targetName);
                Log($"批量移动 {selected.Count} 张图片到分类「{targetName}」");
                RemoveFromCurrentView(models);
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
        LoadCategories(); // 内部末尾已调用 RefreshMemes，无需再调一次
    }

    /// <summary>托盘菜单“设置”：先呼出窗口，再弹设置页</summary>
    public void OpenSettings()
    {
        ShowWindow(activate: true);
        SettingsButton_Click(this, new RoutedEventArgs());
    }

    /// <summary>托盘菜单“显示主窗口”：显示并激活窗口（兼容最小化状态）</summary>
    public void ShowAndActivate()
    {
        ShowWindow(activate: true);
    }

    /// <summary>
    /// 统一“显示主窗口”入口：所有呼出窗口的路径都必须走这里，
    /// 以保证隐藏时停用的拖拽/交互能力被一并恢复（避免从托盘呼出后无法拖拽）。
    /// activate=true 时抢前台焦点（托盘/设置呼出），false 时不抢焦点（快捷键/普通启动，
    /// 保留外部输入框为前台，便于点表情精准投回）。
    /// 幂等：窗口已可见时直接返回，不重复 SW_SHOW / 不重复恢复交互。
    /// flag 与 win32 调用只在本方法内发生，入口回调不得自行 set flag。
    /// </summary>
    public void ShowWindow(bool activate)
    {
        // 已可见（且非最小化）则跳过，避免重复显示导致的状态/清理错位
        if (NativeMethods.IsWindowVisible(_hWnd) && !NativeMethods.IsIconic(_hWnd))
        {
            Log("[窗口] 显示：已可见，跳过");
            _isVisible = true;
            return;
        }

        // 最小化窗口必须用 SW_RESTORE（SW_SHOW 对 iconic 窗口无效）
        if (NativeMethods.IsIconic(_hWnd))
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_RESTORE);
        else if (activate)
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_SHOW);
        else
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_SHOWNOACTIVATE);

        if (activate)
            NativeMethods.SetForegroundWindow(_hWnd);
        // 显示后重新断言置顶，避免最小化/恢复或长期后台后 Z 序被插队
        if (_topMost)
            ApplyTopMost(true);

        _isVisible = true;
        SetMemeViewVisible(true);
        ResumeWindowInteractions();
        Log($"[窗口] 显示完成 (activate={activate})");
    }

    /// <summary>
    /// 统一“隐藏主窗口”入口：所有隐藏窗口的路径都必须走这里，
    /// 以保证拖拽/剪贴板/轮询等回调在隐藏期间被停用，避免触发 native AV。
    /// 幂等：窗口已隐藏时直接返回，不重复 SW_HIDE / 不重复清理。
    /// flag 与 win32 调用只在本方法内发生，入口回调不得自行 set flag。
    /// </summary>
    private void HideWindow()
    {
        // 已隐藏（不可见且非最小化）则跳过，避免重复隐藏导致的清理错位
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

    /// <summary>
    /// 切换窗口可见性（供全局快捷键等“呼出/关闭二合一”入口使用）。
    /// 只依据 Win32 真实状态决策，再委托 ShowWindow/HideWindow 执行；
    /// 自身不写 _isVisible，避免与别的入口状态错位。
    /// </summary>
    private void ToggleWindow()
    {
        bool iconic = NativeMethods.IsIconic(_hWnd);
        bool visible = NativeMethods.IsWindowVisible(_hWnd) && !iconic;
        Log($"[窗口] 切换：当前可见={visible} (IsWindowVisible={NativeMethods.IsWindowVisible(_hWnd)}, Iconic={iconic})");
        if (visible)
            HideWindow();
        else
            ShowWindow(activate: false);
    }

    /// <summary>托盘“退出”：允许真正关闭窗口并退出程序</summary>
    public void RequestExit()
    {
        SaveWindowSize();
        _allowClose = true;
        _isClosing = true;
        this.Close();
    }

    /// <summary>
    /// 开机自启(--hidden)使用：窗口创建后直接隐藏到后台、只留托盘，
    /// 不抢焦点、不激活，避免启动瞬间闪一下界面。
    /// </summary>
    public void StartHidden()
    {
        HideWindow();
    }

    /// <summary>
    /// 普通启动使用：显示窗口但不抢前台焦点（SW_SHOWNOACTIVATE）。
    /// 这样用户正在用的外部应用(QQ 等)仍是前台，_fgTimer 在窗口“可见未激活”
    /// 期间持续记录其窗口句柄，点表情时才能把 Ctrl+V 精准投回输入框。
    /// 若直接 Activate() 抢前台，则 Tapped 时前台已是本窗口，无法拿到外部窗口。
    /// </summary>
    public void ShowWithoutActivate()
    {
        ShowWindow(activate: false);
    }

    // 置顶开关：用户手动切换窗口置顶状态（仅会话内有效，不持久化到 config）
    private void TopMostToggle_Checked(object sender, RoutedEventArgs e)
    {
        _topMost = true;
        ApplyTopMost(true);
        Log("[置顶] 已开启置顶");
    }

    private void TopMostToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _topMost = false;
        ApplyTopMost(false);
        Log("[置顶] 已关闭置顶");
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

    // 搜索框输入防抖：避免每次按键都重建表情列表
    private DispatcherTimer? _searchDebounceTimer;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick -= SearchDebounce_Tick;
        _searchDebounceTimer.Tick += SearchDebounce_Tick;
        _searchDebounceTimer.Start();
    }

    private void SearchDebounce_Tick(object? sender, object e)
    {
        _searchDebounceTimer?.Stop();
        RefreshMemes();
    }

    private async void SettingsFlyout_Closed(object? sender, object e)
    {
        if (SettingsFlyout.Content is SettingsPage page)
        {
            // 若已通过“完成”按钮保存过（_saved），不再重复保存/刷新
            if (!page.IsSaved)
            {
                await page.SaveAsync();
                // 存放路径可能已改变：重新加载分类与表情，反映新路径内容
                LoadCategories();
            }
        }
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ctrl+V：仅在本窗口激活（焦点在主窗口）时，才把剪贴板里的图片导入到分类。
        // 这样截图等写剪贴板的行为不会误触发“粘贴到分类”；无焦点时的 Ctrl+V 仍走投回外部逻辑。
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == Windows.System.VirtualKey.V)
        {
            if (!_isActive)
            {
                // 窗口未激活：不消费 Ctrl+V，放行给外部窗口（沿用原有“投回外部”行为）
                return;
            }
            e.Handled = true;
            await PasteFromClipboardViaShortcutAsync();
            return;
        }

        // Ctrl+F：聚焦搜索框
        if (ctrl && e.Key == Windows.System.VirtualKey.F)
        {
            e.Handled = true;
            SearchBox.Focus(FocusState.Keyboard);
            return;
        }

        // Ctrl+N：新建分类
        if (ctrl && e.Key == Windows.System.VirtualKey.N)
        {
            e.Handled = true;
            await ShowAddCategoryDialog();
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

        // F2：重命名当前选中的分类（聚焦分类控件时）
        if (e.Key == Windows.System.VirtualKey.F2)
        {
            if (CategoryList.SelectedItem is CategoryViewModel selCat)
            {
                e.Handled = true;
                await ShowRenameCategoryDialog(selCat);
            }
            return;
        }

        // Delete：删除当前选中的分类（聚焦分类控件时）
        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            if (CategoryList.SelectedItem is CategoryViewModel selCat)
            {
                e.Handled = true;
                await DeleteCategoryConfirmed(selCat);
            }
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

    // 由 Ctrl+V 主动触发的剪贴板图片导入：先记录内容类型，仅当为图片/位图/文件时才继续，
    // 挡掉文本、HTML、RTF 等非图片/图片路径类内容。
    private async Task PasteFromClipboardViaShortcutAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view == null)
            {
                Log("[粘贴] 触发了 Ctrl+V，但剪贴板为空(GetContent=null)");
                return;
            }

            // 列出当前剪贴板包含的格式，便于排查与打点
            var formats = string.Join(",", view.AvailableFormats);
            Log($"[粘贴] 触发了 Ctrl+V，内容类型: [{formats}]");

            bool hasBitmap = view.Contains(StandardDataFormats.Bitmap);
            bool hasStorageItems = view.Contains(StandardDataFormats.StorageItems);
            if (!hasBitmap && !hasStorageItems)
            {
                Log("[粘贴] 剪贴板非图片/图片路径类内容，已忽略（仅接受 Bitmap / StorageItems）");
                return;
            }

            var category = await PromptCategoryForPasteAsync();
            if (category == null) return;

            var (imported, duplicate) = await ImportFromClipboardAsync(view, category);
            if (imported == null)
                Log("[粘贴] 导入失败或内容为空");
            else if (duplicate)
                Log($"[粘贴] 重复图片已跳过(hash={imported.Hash}, 分类={category})");
            else if (category.Equals(_currentCategory, StringComparison.OrdinalIgnoreCase))
            {
                Log($"[粘贴] 导入成功: {imported.Title} (分类={category})");
                InsertMemeAtFront(new MemeViewModel(imported));
                UpdateCategoryCounts();
            }
        }
        catch (Exception ex)
        {
            Log("[粘贴] PasteFromClipboardViaShortcutAsync 失败: " + ex.Message);
        }
    }

    private void ExitEditMode()
    {
        _editMode = false;
        EditButton.Content = "修改";
        BatchBar.Visibility = Visibility.Collapsed;
        // 退出编辑模式：仅关闭原生多选；拖拽重排保持开启（普通模式也允许排序）
        MemeGridView.SelectedItems.Clear();
        MemeGridView.SelectionMode = ListViewSelectionMode.None;
        _lastShiftAnchor = -1;
        SelectAllButton.Content = "全选";
        // 隐藏并清空复选框指示器
        SetSelectionBoxVisible(false);
        foreach (var vm in _memeList) vm.IsSelected = false;
    }

    // 编辑模式进出时，统一切换所有 item 容器内复选框的可见性。
    // 复选框本身 IsHitTestVisible=False，仅作选中指示，不拦截 Tapped/拖拽。
    // 由于 GridView 虚拟化，容器可能尚未实现，故在下一帧(Dispatcher)再遍历，
    // 并通过可视化树查找 CheckBox（DataTemplate 内 x:Name 不会提升为页面字段）。
    private void SetSelectionBoxVisible(bool visible)
    {
        if (_isClosing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing) return;
            foreach (var item in MemeGridView.Items)
            {
                if (MemeGridView.ContainerFromItem(item) is GridViewItem container)
                {
                    var box = FindCheckBox(container);
                    if (box != null)
                        box.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        });
    }

    // 在容器可视化树中查找复选框（模板根 Grid 内的 SelectionCheckBox）
    private static CheckBox? FindCheckBox(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is CheckBox cb && cb.Name == "SelectionCheckBox")
                return cb;
            var found = FindCheckBox(child);
            if (found != null) return found;
        }
        return null;
    }

    // ---------- 粘贴图片进窗口 ----------
    // 注意：不再监听剪贴板变化（避免截图写剪贴板时误触发“粘贴到分类”），
    // 仅在本窗口激活时由用户主动 Ctrl+V 触发（见 Root_KeyDown）。

    private async Task<string?> PromptCategoryForPasteAsync()
    {
        // 防止高速事件重入：对话框已打开时直接返回
        if (_pasteDialogOpen)
        {
            Log("[剪贴板] 分类对话框重入，跳过");
            return null;
        }
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
            if (result != ContentDialogResult.Primary)
            {
                Log("[剪贴板] 取消粘贴");
                return null;
            }
            var name = box.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                Log("[剪贴板] 分类名为空，取消粘贴");
                return null;
            }

            if (!_categoryList.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                await App.DataEngine.AddCategoryAsync(name);
                _categoryList.Add(new CategoryViewModel(name, 0));
                Log($"[剪贴板] 新建分类 {name}");
            }
            return name;
        }
        finally
        {
            _pasteDialogOpen = false;
        }
    }

    private async Task<(MemeModel? model, bool duplicate)> ImportFromClipboardAsync(DataPackageView view, string category)
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
                        var (m, dup) = await App.DataEngine.ImportMemeAsync(file.Path, category);
                        return (m, dup);
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
                var (imported, dup) = await App.DataEngine.ImportMemeAsync(tempPath, category);
                try { File.Delete(tempPath); } catch { }
                return (imported, dup);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Paste] 导入失败: {ex.Message}");
        }
        return (null, false);
    }

    private static bool IsImage(string ext) =>
        ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

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
            // 最小化/还原会改变真实可见性，统一走 Hide/Show 入口，
            // 由它们维护 _isVisible 与执行清理/恢复，避免状态机旁路错位。
            // 入口只描述意图，不自行 set flag。
            int cmd = (int)wParam & 0xFFF0;
            Log($"WM_SYSCOMMAND: cmd={cmd:X4} (MINIMIZE={NativeMethods.SC_MINIMIZE:X4}, RESTORE={NativeMethods.SC_RESTORE:X4}), _isVisible(before)={_isVisible}");
            if (cmd == NativeMethods.SC_MINIMIZE)
            {
                // 托盘应用：最小化即视为“隐藏”，走统一隐藏入口（SW_HIDE + 清理），
                // 不再走系统最小化（避免任务栏残留）。自行处理故不转交默认过程。
                HideWindow();
                Log($"  _isVisible(after)={_isVisible}");
                return IntPtr.Zero;
            }
            else if (cmd == NativeMethods.SC_RESTORE)
            {
                // 还原后重新断言置顶，避免最小化恢复后 TopMost 偶发失效（参考 PowerToys）
                if (_topMost)
                    ApplyTopMost(true);
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
            // 普通点右上角 X：只隐藏窗口，后台（托盘）继续运行
            if (!_allowClose)
            {
                Log("WM_CLOSE: 仅隐藏窗口（后台保留）");
                HideWindow();
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

    // 切换/断言窗口置顶（参考 PowerToys Always On Top）：
    // 仅用 SetWindowPos 调整 Z 序，不携带 SWP_SHOWWINDOW，与显示/激活逻辑解耦。
    private void ApplyTopMost(bool topMost)
    {
        NativeMethods.SetWindowPos(
            _hWnd,
            topMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
    }

    // 最小化结束事件回调：仅针对本窗口且配置为置顶时，重新 SetWindowPos 置顶一次
    private void WinEventCallback(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == _hWnd && idObject == 0 && _topMost && !_isClosing)
        {
            ApplyTopMost(true);
        }
    }

    // 窗口即将隐藏/销毁时调用：停掉 WinUI 拖拽能力、注销剪贴板监听、
    // 停止前台窗口轮询定时器，防止拖拽会话进行中或隐藏期间这些回调触发
    // XAML 操作导致 native AV(0xc0000005)。closing=true 表示真正销毁窗口，
    // 会置 _isClosing 阻止一切后续异步 XAML 操作。
    private void SuspendWindowInteractions(bool closing)
    {
        if (closing) _isClosing = true;
        // 立即停止预览浮窗（不淡出），避免隐藏/销毁期间其异步回调访问已卸载的可视化树
        HidePreviewPopup(immediate: true, "SuspendWindowInteractions");
        Log($"[防护] SuspendWindowInteractions: closing={closing}, _isVisible={_isVisible}");

        // 停止 WinUI 内置拖拽/重排，让进行中的拖拽会话安全结束
        MemeGridView.CanDragItems = false;
        MemeGridView.CanReorderItems = false;
        MemeGridView.AllowDrop = false;
        CategoryList.CanDragItems = false;
        CategoryList.CanReorderItems = false;
        CategoryList.AllowDrop = false;

        // 停前台窗口轮询定时器
        _fgTimer?.Stop();
    }

    // 窗口重新显示时调用：恢复剪贴板监听、轮询定时器与拖拽能力(编辑模式下)
    private void ResumeWindowInteractions()
    {
        if (_isClosing) return;
        Log($"[防护] ResumeWindowInteractions: _isVisible={_isVisible}, _editMode={_editMode}");

        // 恢复 WinUI 拖拽能力：拖出(CanDragItems)与拖拽重排(CanReorderItems)在
        // 普通模式和编辑模式都需要（普通模式也能在窗口内拖动排序并落库）。
        CategoryList.CanReorderItems = true;
        MemeGridView.CanDragItems = true;
        MemeGridView.CanReorderItems = true;
        MemeGridView.AllowDrop = true;
        CategoryList.CanDragItems = true;
        CategoryList.AllowDrop = true;

        Log("[防护] 已恢复窗口交互");
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        SuspendWindowInteractions(closing: true);
        NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_ID);
        // 注销最小化结束事件钩子，避免泄漏
        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
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
