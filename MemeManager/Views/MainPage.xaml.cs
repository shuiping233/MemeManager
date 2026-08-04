using System.Linq;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using MemeManager.Infrastructure;
using MemeManager.Models;
using MemeManager.Services;
using MemeManager.ViewModels;

namespace MemeManager.Views;

public sealed partial class MainPage : Page, IExternalDropPage, IImageReleasablePage, IImportExportUi, IMemeOperationUi
{
    // 初始化"全部表情"固定项（必须在 LoadCategories 之前，因为 LoadCategories
    // 会触发 SelectionChanged → RefreshMemes → UpdateCategoryCounts 用到 AllMemesVm）

    // 批量操作进度条（顶部 InfoBar）封装；构造时绑定 XAML 控件
    private readonly BatchProgressHelper _batchProgress;

    // 批量操作统一编排器（后台化 + 进度条 + UI 收尾 + 写锁）
    private readonly ImageBatchOperationRunner _batchRunner;

    // 导入/导出业务服务（Phase 3.2）：承接 RunBatchImportAsync / BatchExportCoreAsync / TryGuardWrite，
    // 编排仍委托 _batchRunner，UI 弹窗经 IImportExportUi（本页实现）回 Page。
    private readonly ImportExportService _importExport;

    // 剪贴板服务（Phase 3.3，原 PasteService）：复制图片到剪贴板 / 发到外部窗口。
    private readonly ClipboardService _clipboard = App.GetService<ClipboardService>();

    // 表情写操作服务（Phase 3.4）：承接删除 / 移动 / 移动冲突守卫；编排仍委托 _batchRunner，UI 弹窗经 IMemeOperationUi（本页实现）。
    private readonly MemeOperationService _memeOps;

    // 分类管理服务（Phase 3.5）：承接分类增删改 + 计数计算。
    private readonly CategoryService _categories = App.GetService<CategoryService>();

    private readonly ConfigService ConfigService = App.GetService<ConfigService>();

    // 列表构建/维护策略：复用(ReuseStrategy) 或 重建(RebuildStrategy)。
    // 按配置“启用控件复用策略”在两者间切换，切换立即生效于下一次刷新。
    // 构造函数内会立即按配置初始化；此处给默认实例以满足非空字段。
    private IMemeListStrategy _listStrategy = null!;

    // 便捷属性：当前是否处于“全部表情”视图（判断一律走 Kind，不受文件夹名影响）
    private bool IsAllMemesView => ViewModel.CurrentCategoryKind == CategoryKind.All;

    // 外部拖入/导入时的目标分类：
    // 全部表情视图没有具体归属分类，落入“未分类”兜底分类（不存在则创建）；
    // 普通分类视图则按当前分类导入。
    private string ImportTargetCategory =>
        IsAllMemesView ? AppConstants.UncategorizedCategory : ViewModel.CurrentCategory;

    // 拖拽重排锚点：本次拖起的那一张（e.Items[0]）的文件名。
    // 仅复用策略(ReuseStrategy.ComputeDragOrder)使用，用于把“拖起项”对齐到
    // 鼠标落点，而非 WinUI 默认的组尾对齐；重建策略忽略此值。

    // 全量重载（F5）进行中标记：防止重载与自身/后台写任务并发重建缓存导致崩溃

    // 多选模式：Shift 连续选择的锚点（在 _memeList 中的索引）

    // 防止粘贴导入的分类对话框重入

    private readonly MemeDataEngine _engine =
        App.GetService<MemeDataEngine>();

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    // 悬停放大预览：延迟定时器 + 当前待显示项
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    // 从配置应用悬停预览触发延时（配置缺失时用默认 400ms）
    public void ApplyPreviewDelayFromConfig()
    {
        try
        {
            var cfg = ConfigService.Config;
            int ms = cfg?.PreviewDelayMs > 0 ? cfg.PreviewDelayMs : 400;
            _previewTimer.Interval = TimeSpan.FromMilliseconds(ms);
        }
        catch { }
    }

    public MainPage()
    {
        InitializeComponent();

        DataContext = App.GetService<MainViewModel>();

        // Phase 2：把按钮/意图入口从 Click 迁到 VM Command；依赖窗口/XamlRoot/页面状态的
        // UI 副作用通过 VM 的委托属性（Action/Func）回调本页。单 Page 对接场景下用 '=' 赋值，
        // 新 Page 实例会覆盖旧的，VM（单例）上永远只有 1 份，天然不累积（根除 R3 泄漏）。
        WireViewModelEvents();

        _batchProgress = new BatchProgressHelper(BatchProgressInfoBar, BatchProgressBar, BatchProgressCount, BatchProgressText);

        _batchRunner = new ImageBatchOperationRunner(_batchProgress, DispatcherQueue, new BatchUiContext
        {
            IsClosing = () => App.MainWindow.IsClosing,
            IsVisible = () => App.MainWindow.IsAppVisible,
            CurrentCategory = () => ViewModel.CurrentCategory,
            IsAllMemesView = () => IsAllMemesView,
            UpdateCategoryCounts = UpdateCategoryCounts,
            RefreshMemes = RefreshMemes,
            RemoveFromCurrentView = RemoveFromCurrentView,
        });

        _importExport = new ImportExportService(_engine, _batchRunner, this);
        _memeOps = new MemeOperationService(_engine, _batchRunner, this);

        // 按配置选择列表构建策略（复用 / 重建）。切换在设置页保存后即时应用。
        ApplyListStrategyFromConfig();

        _previewTimer.Tick += PreviewTimer_Tick;
        ApplyPreviewDelayFromConfig();

        // 置顶开关：与窗口当前置顶状态同步（窗口级状态由 MainWindow 持有）
        TopMostToggle.IsChecked = App.MainWindow.IsTopMost;

        // 订阅数据目录文件监听：图片从库中消失/新增（外部拖出/被删/手动加图）时，
        // 就地更新对应分类控件并提示用户（与引擎解耦，逻辑全在页面层）。
        if (_engine.Watcher != null)
        {
            _engine.Watcher.FilesRemoved += OnWatchedFilesRemoved;
            _engine.Watcher.FilesAdded += OnWatchedFilesAdded;
            _engine.Watcher.FilesMoved += OnWatchedFilesMoved;
        }

        CategoryList.ItemsSource = ViewModel.CategoryList;
        MemeGridView.ItemsSource = ViewModel.MemeList;

        SettingsFlyout.Closed += SettingsFlyout_Closed;

        // 键盘事件由 MainWindow 内容根转发（见 MainWindow.ForwardKeyDown），
        // 否则无任何控件获焦时按键不会冒泡到 Page。
        this.Loaded += (_, _) =>
        {
            RootGrid.Focus(FocusState.Programmatic);
            // Full 模式标题栏：把顶部空拖拽条（TitleStrip）注册为窗口标题栏区域。
            // 仅此条可拖拽窗口；工具栏/搜索框在下方客户端区，不受系统三键覆盖影响。
            App.MainWindow.SetTitleBarElement(TitleStrip);
            // 标题文本由 XAML 绑定到 MainWindow.WindowTitle（含 AppName + 版本），此处无需赋值。

            // 标题条 Logo：用发布必拷贝的 AppIcon.ico（与托盘图标同源，见 MainWindow.AppIconPath），
            // 以绝对路径加载，避免 XAML 松散引用 png 在非打包发布时丢失。
            try
            {
                var iconPath = AppConstants.IconPath;
                if (iconPath != null)
                    TitleStripLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
            }
            catch (Exception ex) { Logger.Log("[标题条] 加载 Logo 失败: " + ex.Message); }
        };

        // 必须先给 AllMemesList 赋 ItemsSource，再 LoadCategories（其内部会选中 AllMemesVm），
        // 否则构造期选中因 ItemsSource 为 null 而失效（#7）。
        AllMemesList.ItemsSource = new ObservableCollection<CategoryViewModel> { ViewModel.AllMemesVm };

        LoadCategories();

        // 页面重建（Full↔Mini 切换会 new 新 MainPage）后与单例 VM 的编辑模式状态对齐：
        // 若 VM 仍处于编辑模式，补齐编辑模式 UI（按钮文案/批量栏），
        // 避免出现"非编辑外观 + 右上角复选框"的错位显示（复选框中残留 bug）。
        if (ViewModel.EditMode)
        {
            EditButton.Content = Localization.Get("Meme_Done");
            BatchBar.Visibility = Visibility.Visible;
        }
    }

    // 把 VM 的"请求"委托属性接到本页的 UI 实现。全部用 '=' 赋值（非 +=）：MainViewModel 是
    // 单例且只被本页对接，'=' 覆盖式天然不累积，新页实例替换旧引用后旧页可被 GC，
    // 无需在 Unloaded 里 -= 反订阅（根除单例 VM 事件累积泄漏 R3）。
    private void WireViewModelEvents()
    {
        // Phase 2.1：刷新入口（业务逻辑 RefreshDataAsync 仍留本页）
        ViewModel.RefreshRequested = RefreshDataAsync;
        // Phase 2.2：设置浮窗（UI 行为留本页）
        ViewModel.SettingsRequested = ShowSettingsFlyout;
        // Phase 2.3：Mini 模式（切窗口模式 UI 行为留本页）
        ViewModel.MiniModeRequested = SwitchToMiniMode;
        // Phase 2.4：编辑（多选）模式切换
        ViewModel.EditModeRequested = ToggleEditMode;
        // Phase 2.5：全选/取消全选
        ViewModel.SelectAllRequested = ToggleSelectAll;
        // Phase 2.6：新建分类（与分类右键共用入口）
        ViewModel.NewCategoryRequested = async () => await ShowAddCategoryDialog();
        // 2.7：分类数据变更后刷新表情列表（VM 只发通知，刷新逻辑留本页）
        ViewModel.CategoriesChangedRequested = () =>
        {
            // 同步分类栏选中态：删除分类后 CurrentCategory 已切到新分类，
            // 需让 ListView.SelectedItem 跟随，触发 SelectionChanged 恢复焦点/写配置/刷新。
            CategoryList.SelectedItem = CategoryList.Items.Cast<CategoryViewModel>()
                .FirstOrDefault(c => c.Name.Equals(ViewModel.CurrentCategory, StringComparison.OrdinalIgnoreCase));
            RefreshMemes();
        };
        // 2.7：删除分类确认弹窗（VM 无 XamlRoot，弹窗 UI 留本页）
        ViewModel.ConfirmDeleteCategoryRequested = async cat =>
        {
            var result = await DialogHelper.ConfirmDeleteCategoryAsync(XamlRoot, cat.Name);
            return result == ContentDialogResult.Primary;
        };
        // 2.7：重命名分类输入弹窗
        ViewModel.PromptRenameCategoryRequested = cat
            => DialogHelper.PromptRenameCategoryAsync(XamlRoot, cat.Name);
        // 2.7：重命名分类失败提示
        ViewModel.RenameCategoryFailedRequested = cat
            => DialogHelper.ShowCategoryExistsAsync(XamlRoot, cat.Name);

        // 2.8：表情项级命令（VM 只发请求，弹窗/批量写/选中项等依赖 Page 状态的逻辑留本页）
        ViewModel.EnterEditModeAndSelectRequested = vm => EnterEditModeAndSelect(vm);
        ViewModel.PromptRenameMemeRequested = vm
            => DialogHelper.PromptRenameMemeAsync(XamlRoot, vm.Title);
        ViewModel.DeleteMemeRequested = vm => _ = DeleteMemeCoreAsync(vm);
        ViewModel.BatchImportRequested = async () => await BatchImportCoreAsync();
        ViewModel.BatchExportRequested = async () => await BatchExportCoreAsync();
        ViewModel.BatchDeleteRequested = async () => await DeleteSelectedMemesAsync();
        // 2.9：单击表情项转发到 VM 命令；隐藏预览浮窗、发送到外部窗口等 UI 行为留本页
        ViewModel.HidePreviewRequested = () => HidePreviewPopup(reason: "Tapped");
        ViewModel.PasteToExternalRequested = async vm =>
        {
            var target = App.MainWindow.ResolveExternalPasteTarget();
            if (target == IntPtr.Zero)
            {
                Log("单击(发送模式): 未解析到有效外部窗口，取消本次粘贴");
                return;
            }
            Log($"单击(发送模式): 发送图片 {vm.Title} 到前台窗口 target={target}");
            await _clipboard.OutputMemeToCursorAsync(vm.LocalPath, target);
            await _engine.IncrementUsageAsync(vm.Hash);
        };
    }

    // ---------- 分类 ----------

    // 供设置页在“浏览”修改存放路径后即时刷新（分类/表情）
    public void ReloadData()
    {
        // 重载（设置保存/Mini→Full）场景：以内存当前分类为准，不被尚未 flush 的旧 config 覆盖。
        LoadCategories(restoreSelectionFromConfig: false);
    }

    // 按配置创建对应的列表策略实例。
    private IMemeListStrategy CreateStrategy(bool reuse) =>
        reuse ? new ReuseStrategy(_engine) : new RebuildStrategy(_engine);

    // 从配置读取并应用列表策略；首次启动与“设置”保存后均会调用。
    // 复用模式切换会打日志，便于观察内存/行为变化。
    public void ApplyListStrategyFromConfig()
    {
        bool reuse = ConfigService.Config.UseControlReuse;
        var prev = _listStrategy;
        _listStrategy = CreateStrategy(reuse);
        if (prev != null)
            Log($"[策略] 列表策略切换为: {(reuse ? "复用(Reuse)" : "重建(Rebuild)")}");
        else
            Log($"[策略] 列表策略初始化为: {(reuse ? "复用(Reuse)" : "重建(Rebuild)")}");

        // Mini 按钮可见性随“允许 Mini 模式”配置变化（设置页保存后立即生效）。
        ApplyMiniModeVisibilityFromConfig();
    }

    // restoreSelectionFromConfig=true：初次加载/启动，按 config 的 LastCategory 恢复选中（防抖可能未落盘，但启动时不依赖内存）。
    // restoreSelectionFromConfig=false：刷新/重载（如 F5、设置保存），此时内存中的当前分类才是真值，
    // 不能用尚未 flush 的旧 config 覆盖（否则会把刚切走的分类又切回去，见 https://github.com/shuiping233/MemeManager/issues/16 相关回归）。
    private void LoadCategories(bool restoreSelectionFromConfig = true)
    {
        // 若没有任何分类文件夹，默认创建一个 "Default"
        if (_engine.GetCategories().Count == 0)
        {
            _engine.EnsureDefaultCategory();
        }

        // 用当前策略同步分类列表（复用=增量复用容器，重建=整体重建）。
        // 具体算法封装在 IMemeListStrategy.SyncCategories 内。
        _listStrategy.SyncCategories(
            ViewModel.CategoryList,
            _engine.GetCategories(),
            cat => _engine.GetMemes(cat).Count);

        if (restoreSelectionFromConfig)
        {
            // 默认选中上次或第一项。
            // 注意：重建模式下 SyncCategories 会整体 Clear+新建 VM 并销毁旧容器，选中视觉（蓝条/高亮）
            // 随之丢失，因此必须无条件重新赋值 SelectedItem 才能恢复高亮；仅当 target 与当前选中同名时
            // 跳过，避免无谓触发 SelectionChanged（复用模式下这一支基本不会命中，因为容器未重建）。
            var (lastKind, lastName) = ConfigService.Config.LastCategory.Resolve();
            if (lastKind != CategoryKind.Normal)
            {
                // 上次停留在虚拟分类（如"全部表情"）：选中对应固定项并刷新为聚合视图
                if (lastKind == CategoryKind.All)
                {
                    AllMemesList.SelectedItem = ViewModel.AllMemesVm;
                    ViewModel.CurrentCategory = CategoryKind.All.VirtualName()!;
                    ViewModel.CurrentCategoryKind = CategoryKind.All;
                }
                // 将来新增虚拟分类（如 Recent）在此加分支
            }
            else
            {
                var target = ViewModel.CategoryList.FirstOrDefault(c => c.Name == lastName) ?? ViewModel.CategoryList.FirstOrDefault();
                if (target != null && !target.Name.Equals(ViewModel.CurrentCategory, StringComparison.OrdinalIgnoreCase))
                {
                    CategoryList.SelectedItem = target;
                    ViewModel.CurrentCategory = target.Name;
                    ViewModel.CurrentCategoryKind = CategoryKind.Normal;
                }
                else if (target != null)
                {
                    // 重建模式下分类名没变但容器已重建：重新设回同一项以恢复选中视觉。
                    CategoryList.SelectedItem = target;
                }
            }
        }
        else
        {
            // 刷新场景：以内存中的当前分类为准，重新断言选中（容器可能被重建，需重设以恢复高亮）。
            if (ViewModel.CurrentCategoryKind == CategoryKind.All)
            {
                AllMemesList.SelectedItem = ViewModel.AllMemesVm;
            }
            else
            {
                var target = ViewModel.CategoryList.FirstOrDefault(c => c.Name == ViewModel.CurrentCategory);
                if (target != null)
                    CategoryList.SelectedItem = target;
            }
        }

        RefreshMemes();
        SyncMemeDragState();

        // 若当前处于编辑模式（如设置里切换了多选风格后重载），需重新应用 SelectionMode 与复选框，
        // 否则 SelectionMode 停留在旧值、复选框可能消失或重复（#23）。
        if (ViewModel.EditMode)
            ReapplyEditModeState();
    }

    // 重新应用编辑模式下的列表选择模式与自绘复选框可见性（配置可能已变更）。
    private void ReapplyEditModeState()
    {
        bool explorerStyle = ConfigService.Config.ExplorerStyleMultiSelect;
        MemeGridView.SelectionMode = explorerStyle
            ? ListViewSelectionMode.Extended
            : ListViewSelectionMode.Multiple;
        ApplySelectionBoxVisibility();
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is CategoryViewModel cat)
        {
            // 清除"全部表情"的选中态
            AllMemesList.SelectedItem = null;
            // 分类未变（如重复选中同一项）则跳过整段重建，避免无谓分配
            if (cat.Name.Equals(ViewModel.CurrentCategory, StringComparison.OrdinalIgnoreCase))
                return;
            ViewModel.CurrentCategory = cat.Name;
            ViewModel.CurrentCategoryKind = CategoryKind.Normal;
            DebouncedSaveLastCategory(cat.Name);
            RefreshMemes();
            SyncMemeDragState();
        }
    }

    private static readonly object _lastCatLock = new();
    private static Timer? _lastCatTimer;
    private static string? _pendingLastCategory;

    internal static void DebouncedSaveLastCategory(string category)
    {
        lock (_lastCatLock)
        {
            _pendingLastCategory = category;
            _lastCatTimer ??= new Timer(_ => FlushLastCategory(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _lastCatTimer.Change(AppConstants.LastCategorySaveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    public static void FlushLastCategory()
    {
        string? pending = null;
        lock (_lastCatLock)
        {
            pending = _pendingLastCategory;
            _pendingLastCategory = null;
            _lastCatTimer?.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
        }
        if (pending != null)
        {
            var engine = App.GetService<MemeDataEngine>();
            _ = engine.UpdateConfigAsync(c => c.LastCategory = pending);
        }
    }

    // 依据当前视图同步网格拖拽能力：
    // “全部表情”视图下项来自不同分类，禁止拖起（视觉上即不可拖，也从根上阻断跨分类重排/移动）；
    // 普通分类视图下允许拖出/拖入。编辑模式与此无关（普通模式本就允许拖出）。
    private void SyncMemeDragState()
    {
        // “全部表情”视图下项来自不同分类：
        //  - 允许拖出到外部进程（QQ/资源管理器），故 CanDragItems 开启、语义为 Copy；
        //  - 禁止任何内部拖拽（重排/移动到分类栏/拖回网格），故 CanReorderItems 关闭，
        //    且内部落点的 DragOver 已对全部表情返回 None 显示禁止光标。
        // 普通分类视图下两者皆开（可拖出、可内部重排）。
        MemeGridView.CanDragItems = true;          // 始终允许拖出到外部
        MemeGridView.CanReorderItems = !IsAllMemesView; // 仅全部表情下禁止内部重排
    }

    private void AllMemesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AllMemesList.SelectedItem != null)
        {
            CategoryList.SelectedItem = null;
            // 切到"全部表情"：标记为 All 视图，RefreshMemes 内部会拉取全部表情。
            // 分类未变（如重复选中同一项）则跳过整段重建，避免无谓分配。
            if (!IsAllMemesView)
            {
                ViewModel.CurrentCategory = CategoryKind.All.VirtualName()!;
                ViewModel.CurrentCategoryKind = CategoryKind.All;
                DebouncedSaveLastCategory(CategoryKind.All.VirtualName()!);
                RefreshMemes();
                SyncMemeDragState();
            }
        }
    }

    // 拖拽图片到分类列表：仅接受内部移动，并高亮可放置
    private void CategoryList_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.DraggingMemes != null && ViewModel.DraggingMemes.Count > 0)
        {
            // “全部表情”视图下项来自不同分类，禁止拖到分类栏移动归属：显示禁止光标。
            if (IsAllMemesView)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.DragUIOverride.Caption = Localization.Get("AllMemes_DropNotAllowed");
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
                return;
            }

            // 拖入表情图片：CanReorderItems 已在 MemeGridView_DragItemsStarting 提前关闭
            // （会话早期关闭才能避免插入占位撑开），此处无需再关；
            // 不关闭 CanDragItems，以保证分类项仍能作为 drop 目标接收图片（移动到该分类）。
            // 与 DragItemsStarting 的 RequestedOperation=Move 保持一致，否则 WinUI 认为不兼容显示禁止符号
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = Localization.Get("Meme_MoveToThisCategory");
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            // 分类自身重排序：开启 CanReorderItems，允许占位撑开动画
            CategoryList.CanReorderItems = true;
            e.AcceptedOperation = DataPackageOperation.Move;
        }
    }

    // 分类列表内部拖拽重排完成：WinUI 已把 _categoryList 排好，读顺序写回 .metadata.json
    private async void CategoryList_DragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
    {
        // 重排结束，恢复 CanReorderItems（image 拖入时曾被临时关闭）
        CategoryList.CanReorderItems = true;
        if (e.DropResult != DataPackageOperation.Move &&
            e.DropResult != DataPackageOperation.Copy)
            return;

        var ordered = ViewModel.CategoryList.Select(c => c.Name).ToList();
        await _engine.ReorderCategoriesAsync(ordered);
        Log($"分类重排写回 {ordered.Count} 个分类顺序");
    }

    private async void CategoryListItem_Drop(object sender, DragEventArgs e)
    {
        // “全部表情”视图下禁止拖到分类栏移动归属（DragOver 已显示禁止光标，这里兜底）。
        if (IsAllMemesView)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        // 优先用 DragItemsStarting 记录的 _draggingMemes；
        // 若它已被 DragItemsCompleted 提前清空（跨控件拖拽事件顺序不确定），
        // 则从 e.DataView 的 StorageItems 还原被拖项，避免依赖共享字段。
        List<MemeModel> memes;
        if (ViewModel.DraggingMemes != null && ViewModel.DraggingMemes.Count > 0)
        {
            memes = ViewModel.DraggingMemes;
            ViewModel.DraggingMemes = null;
        }
        else
        {
            memes = await MemesFromDataViewAsync(e.DataView);
            if (memes.Count == 0)
            {
                e.AcceptedOperation = DataPackageOperation.None;
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
            // 写锁守卫 + 冲突守卫 + 后台移动均委托 MemeOperationService
            await _memeOps.MoveMemesAsync(memes, targetCat.Name);
        }
        e.AcceptedOperation = DataPackageOperation.Move;
    }

    /// <summary>将键盘焦点设置到搜索输入框（供窗口从托盘呼出后调用）</summary>
    public void FocusSearchBox()
    {
        SearchBox.Focus(FocusState.Programmatic);
    }

    // 窗口显示/隐藏时由 MainWindow 调用：控制列表 ItemsSource 的挂载与释放。
    // 可见时重新绑回数据源并恢复选中视觉；隐藏时断开 ItemsSource 释放 GPU 纹理。
    public void SetMemeViewVisible(bool visible)
    {
        if (visible)
        {
            bool rebind = MemeGridView.ItemsSource != ViewModel.MemeList;
            if (rebind)
            {
                CategoryList.ItemsSource = ViewModel.CategoryList;
                MemeGridView.ItemsSource = ViewModel.MemeList;
            }
            // 隐藏时 CategoryList.ItemsSource 被置空导致选中容器销毁、蓝条/高亮丢失；
            // 重新绑回后必须重新断言选中，待容器生成后再设回以恢复视觉。
            // 但若 ItemsSource 未重绑且 sel 已是当前选中项，则跳过：避免无谓的
            // 取消选中→重新选中视觉切换（容器回收 + 鼠标悬停易触发 ListViewBaseItemChrome 崩溃）。
            if (ViewModel.CurrentCategoryKind == CategoryKind.All)
            {
                // 虚拟分类（如“全部表情”）：恢复对应的虚拟列表项选中，不回退到普通分类、不改写 LastCategory。
                if (rebind || AllMemesList.SelectedItem != ViewModel.AllMemesVm)
                {
                    AllMemesList.SelectedItem = ViewModel.AllMemesVm;
                }
            }
            else
            {
                var sel = ViewModel.CategoryList.FirstOrDefault(c => c.Name == ViewModel.CurrentCategory)
                          ?? ViewModel.CategoryList.FirstOrDefault();
                if (sel != null && (rebind || CategoryList.SelectedItem != sel))
                {
                    CategoryList.SelectedItem = null;
                    DispatcherQueue.TryEnqueue(() => { CategoryList.SelectedItem = sel; });
                }
            }
        }
        else
        {
            // 隐藏时断开图像资源引用（GPU 纹理随后由框架回收）；GC 由 MainWindow 统一执行。
            // 摘容器（ItemsSource=null 卸载 Image、释放 GPU 纹理）在 ReleaseImages 内完成，
            // 由 HideWindow 以 detachItemsSource:true 驱动。
            ReleaseImages(detachItemsSource: true);
        }
    }

    // IImageReleasablePage：仅断引用，不 GC（GC 由 MainWindow 在隐藏/切模式后统一调用）。
    // detachItemsSource=true 仅在隐藏窗口时使用（视觉树保留）：摘掉网格 ItemsSource 让
    // Image 容器卸载、GPU 纹理被框架回收；切模式（视觉树即将被导航卸载）传 false，避免
    // 在导航前扰动容器状态（曾导致切回空白，85eb33c 即因此误删此步）。
    public void ReleaseImages(bool detachItemsSource)
    {
        // 释放图像资源：遍历表情 VM 断开其 BitmapImage 引用（不触发 PropertyChanged，
        // 避免绑定重求又 new 出纹理）。
        foreach (var vm in ViewModel.MemeList)
            vm.ClearImages();

        // 仅清 VM 字段不够——Image.Source 仍引用旧 BitmapImage，GPU 纹理不会释放；
        // 摘容器后 Image 从可视化树移除，WinUI 框架在下一帧释放纹理。
        if (detachItemsSource)
            MemeGridView.ItemsSource = null;

        // 仅复用模式下打印内存诊断（重建模式无需关注 VM 常驻情况）。
        if (ConfigService.Config.UseControlReuse)
        {
            Log($"[内存诊断] 隐藏释放(复用模式): ViewModel.MemeList={ViewModel.MemeList.Count} ViewModel.CategoryList={ViewModel.CategoryList.Count} " +
                $"VM存活BitmapImage={MemeViewModel.LiveBitmapImageCount} " +
                $"托管堆={GC.GetTotalMemory(false) / 1024}KB GC代数0/1/2={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
        }
        HidePreviewPopup(true, "ReleaseImages");
    }

    private void HidePreviewPopup(bool immediate = false, string reason = "")
    {
        _previewTimer.Stop();
        ViewModel.PendingPreviewVm = null;
        ViewModel.PendingPreviewAnchor = null;

        if (!PreviewPopup.IsOpen)
        {
            _previewFadingOut = false;
            return;
        }

        // 窗口隐藏/销毁等场景直接关闭，不做淡出
        if (immediate || App.MainWindow.IsClosing)
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
            var all = _engine.GetAllMemes();
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
        if (ViewModel.DraggingMemes != null && ViewModel.DraggingMemes.Count > 0)
        {
            // 与 DragItemsStarting 的 RequestedOperation=Move 保持一致
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = Localization.Get("Meme_MoveToThisCategory");
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    // 新增分类对话框：成功后在列表末尾追加并选中
    private async Task ShowAddCategoryDialog()
    {
        var name = await DialogHelper.PromptNewCategoryAsync(this.XamlRoot);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (ViewModel.CategoryList.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            await DialogHelper.ShowCategoryExistsAsync(this.XamlRoot, name);
            return;
        }
        // 非法分类名（"."/".."、路径分隔符、设备名等）弹窗提示，不静默建兜底分类
        if (!FileNameValidator.IsValidCategoryName(name))
        {
            await DialogHelper.ShowInvalidCategoryNameAsync(this.XamlRoot, name);
            return;
        }
        bool added = await _categories.AddCategoryAsync(name);
        if (added)
        {
            ViewModel.CategoryList.Add(new CategoryViewModel(name, 0));
            CategoryList.SelectedItem = ViewModel.CategoryList.Last();
        }
    }

    // ---------- 表情渲染 ----------

    private void RefreshMemes()
    {
        var keyword = SearchBox.Text?.Trim();
        var memes = ViewModel.QueryMemes(
            IsAllMemesView ? null : ViewModel.CurrentCategory, keyword);

        // 用当前策略刷新表情列表（复用=增量复用 VM，重建=整体 Clear+重建）。
        _listStrategy.RefreshMemes(ViewModel.MemeList, memes);

        // 复用语义下记录增量统计，便于诊断（重建模式为全量重建，无增量可记）。
        if (_listStrategy is ReuseStrategy)
        {
            int newCount = memes.Count;
            int oldCount = ViewModel.MemeList.Count;
            Log($"[诊断] RefreshMemes VM数={ViewModel.MemeList.Count} 新项数={newCount}");
        }

        UpdateCategoryCounts();

        // 编辑模式下列表重建(如搜索/刷新)后，按当前配置重新显示/隐藏复选框并把原生选中态镜像回新 VM
        if (ViewModel.EditMode)
        {
            ApplySelectionBoxVisibility();
            SyncSelectionToViewModels();
        }

        UpdateEmptyHint();
    }

    // 空状态提示：图片列表为空时居中显示“当前分类没有图片”（搜索无结果时显示对应文案），
    // 避免用户误以为图片没加载出来。文本走 i18n。
    private void UpdateEmptyHint()
    {
        if (ViewModel.MemeList.Count > 0)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            return;
        }

        var keyword = SearchBox.Text?.Trim();
        EmptyHint.Text = string.IsNullOrWhiteSpace(keyword)
            ? Localization.Get("Meme_EmptyHint")
            : string.Format(Localization.Get("Meme_SearchEmptyHint"), keyword);
        EmptyHint.Visibility = Visibility.Visible;
    }

    // 精准从当前视图移除若干项（不 Clear 重建，保持滚动条位置与选中状态）。
    // 用于“移动到其他分类”等“内容减少但顺序不变”的场景。
    private void RemoveFromCurrentView(IEnumerable<MemeModel> removed)
    {
        var names = new HashSet<string>(
            removed.Select(m => m.FileName), StringComparer.OrdinalIgnoreCase);
        for (int i = ViewModel.MemeList.Count - 1; i >= 0; i--)
            if (names.Contains(ViewModel.MemeList[i].FileName))
                ViewModel.MemeList.RemoveAt(i);

        UpdateEmptyHint();
    }

    private void UpdateCategoryCounts()
    {
        // 计数计算委托 CategoryService（基于内存缓存），此处仅把结果写回 VM 对象。
        // 注意：操作后分类可能变为 0 张，此时 counts 不再包含该分类名（ComputeCounts 只统计有图的分类），
        // 必须用 TryGetValue 取“有则值、无则 0”，否则 0 张的分类计数会残留旧值不刷新（#计数清零 bug）。
        var (counts, total) = _categories.ComputeCounts();
        foreach (var c in ViewModel.CategoryList)
            c.Count = counts.TryGetValue(c.Name, out int n) ? n : 0;
        // 更新"全部表情"总数
        ViewModel.AllMemesVm.Count = total;
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
        if (ViewModel.EditMode || !App.MainWindow.IsAppVisible || App.MainWindow.IsClosing || ViewModel.DraggingMemes != null) return;
        // 文件选择器打开期间不弹预览浮窗（避免对话框抢焦点后误触发）
        if (App.MainWindow.IsFilePickerOpen) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not MemeViewModel vm) return;

        _lastPointerPos = e.GetCurrentPoint(RootGrid).Position;

        // 若浮窗已开（且未在淡出），直接切换内容/位置并淡入，无需再等延时
        if (PreviewPopup.IsOpen && !_previewFadingOut)
        {
            ShowPreviewPopup(vm, fe);
        }
        else
        {
            ViewModel.PendingPreviewVm = vm;
            ViewModel.PendingPreviewAnchor = fe;
            _previewTimer.Start();
        }
    }

    private void MemeItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _previewTimer.Stop();
        ViewModel.PendingPreviewVm = null;
        ViewModel.PendingPreviewAnchor = null;
        // 鼠标离开表情项即关闭预览（移动即取消，不依赖命中测试）
        HidePreviewPopup(reason: "PointerExited");
    }

    // 鼠标在窗口内移动：预览只是临时提示，鼠标真正移动（超过阈值）即取消。
    // 用距离阈值过滤“静止时 WinUI 仍会派发的 PointerMoved 抖动”，避免没动却开关。
    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!PreviewPopup.IsOpen) return;

        var pt = e.GetCurrentPoint(RootGrid).Position;
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
        Log($"[预览] TimerTick -> Show (pending={ViewModel.PendingPreviewVm?.Title})");
        _previewTimer.Stop();
        if (ViewModel.PendingPreviewVm == null || ViewModel.PendingPreviewAnchor == null) return;
        if (App.MainWindow.IsClosing || !App.MainWindow.IsAppVisible) return;

        ShowPreviewPopup(ViewModel.PendingPreviewVm, ViewModel.PendingPreviewAnchor);
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

            var workArea = App.MainWindow.GetWorkAreaInWindowCoords();
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

    // 取得元素相对【页面根】的矩形（DIP）。Popup 的 Offset 是窗口相对坐标，
    // 页面充满整个窗口，因此以页面根为参考系与窗口坐标一致。
    private Windows.Foundation.Rect GetElementWindowRect(FrameworkElement element)
    {
        var transform = element.TransformToVisual(RootGrid);
        var topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        return new Windows.Foundation.Rect(topLeft.X, topLeft.Y, element.ActualWidth, element.ActualHeight);
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

    private void ToggleEditMode()
    {
        if (ViewModel.EditMode)
        {
            Log("退出多选模式");
            ExitEditMode();
        }
        else
        {
            EnterEditMode();
        }
    }

    // 统一进入编辑（多选）模式：所有入口（修改按钮 / Shift+点击 / 右键多选 / Ctrl+A）都走这里，
    // 不再散落设置 _editMode 与各 UI 状态，避免多处野 flag 设置错位。
    private void EnterEditMode()
    {
        if (ViewModel.EditMode) return;
        Log("进入多选模式");
        ViewModel.EditMode = true;
        EditButton.Content = Localization.Get("Meme_Done");
        // 背景/前景的蓝色由 XAML 写死常亮，这里不再处理颜色，仅切换文字与模式
        BatchBar.Visibility = Visibility.Visible;
        // 编辑模式不再单独开启重排：重排能力统一由 SyncMemeDragState 收口
        // （普通分类视图允许、全部表情视图禁止），避免进编辑模式时误开全部表情的重排。
        // 多选模式由配置决定：
        //  - false：资源管理器风格 ListViewSelectionMode.Multiple（系统自带复选框），隐藏自绘复选框
        //  - true ：ListViewSelectionMode.Extended + 自绘右上角复选框，支持 shift 连续/反选
        bool explorerStyle = ConfigService.Config.ExplorerStyleMultiSelect;
        MemeGridView.SelectionMode = explorerStyle
            ? ListViewSelectionMode.Extended
            : ListViewSelectionMode.Multiple;
        // 仅 Extended 模式显示我们自绘的复选框
        ApplySelectionBoxVisibility();
    }

    // 进编辑模式并选中指定图片（Shift+点击 / 右键“多选”共用）
    private void EnterEditModeAndSelect(MemeViewModel vm)
    {
        EnterEditMode();
        MemeGridView.SelectedItems.Clear();
        MemeGridView.SelectedItems.Add(vm);
    }

    // 进编辑模式并全选（Ctrl+A 在非编辑模式时触发）
    private void EnterEditModeAndSelectAll()
    {
        EnterEditMode();
        ToggleSelectAll();
    }

    // ---------- 点击表情（非修改模式 = 粘贴；多选模式 = 切换选中） ----------
    // 用 Tapped 而非 PointerPressed：拖拽(CanDrag)会取消 Tapped，避免“先单击粘贴一次、再拖出又粘贴一次”

    // 单击表情项：薄适配器，把 Tapped 事件转发给 VM 的 MemeTappedCommand（逻辑已迁入 VM）。
    private void MemeItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MemeViewModel clicked)
            ViewModel.MemeTappedCommand.Execute(clicked);
    }

    // 容器(项)为数据生成时：若处于编辑模式且为 Extended 风格，立即把复选框设为可见，
    // 解决虚拟化下滚动后新出现的 item 默认 Collapsed 的问题。
    // Explorer 风格(Multiple)下不显示自绘复选框（用系统自带）。
    private void MemeGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Phase == 0 && args.ItemContainer != null)
        {
            var box = FindCheckBox(args.ItemContainer);
            if (box != null)
                box.Visibility = (ViewModel.EditMode && ConfigService.Config.ExplorerStyleMultiSelect)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    // GridView 原生选中变化 → 更新批量操作按钮可用状态，并镜像到 VM(驱动右上角复选框)
    private void MemeGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncSelectionToViewModels();
        UpdateBatchButtons();
        UpdateSelectAllButton();
    }

    // 把 GridView 原生选中态单向镜像到各 MemeViewModel.IsSelected，
    // 供 ItemTemplate 里的 CheckBox 显示勾选(纯指示，不反向写回)。
    // 先整体清一遍再按 SelectedItems 置位，覆盖“反选/全清”等场景。
    private void SyncSelectionToViewModels()
    {
        if (App.MainWindow.IsClosing) return;
        var selected = new HashSet<MemeViewModel>(
            MemeGridView.SelectedItems.Cast<MemeViewModel>());
        foreach (var vm in ViewModel.MemeList)
            vm.IsSelected = selected.Contains(vm);
    }

    // ---------- 拖拽：拖入导入 / 拖出到外部输入框 ----------

    private static void Log(string msg) => Logger.Log($"[MemeManager] {msg}");

    private void MemeGridView_DragOver(object sender, DragEventArgs e)
    {
        // 内部 item 拖回网格自身（全部表情下禁止任何内部拖拽移动/重排）：显示禁止光标。
        if (ViewModel.DraggingMemes != null && ViewModel.DraggingMemes.Count > 0)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        // 拒绝"无数据格式"的拖拽（如左侧分类栏项拖入图片区）：DraggingMemes 为空
        // 且不含 StorageItems/Bitmap 即非外部文件/图片拖入。此前无条件接受 Copy，
        // 而 CanReorderItems 为 true 时 WinUI 会显示插入占位把网格撑开一个位置
        // （分类拖入实际无效果，纯视觉 bug）。
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)
            && !e.DataView.Contains(StandardDataFormats.Bitmap))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        // 接受外部拖入（如从文件管理器拖图片进来做导入），确保 Drop 能触发。
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = false;
    }

    // ---------- 拖拽项事件（WinUI 内置重排 / 拖出） ----------

    // 项开始被拖出（编辑模式或非编辑模式都会触发，因为 CanDragItems=True）。
    // 记录被拖项 + 设 StorageItems（供拖到文件管理器/输入框时复制）。
    private void MemeGridView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // 全量刷新（F5）重建 ItemsSource 期间禁止发起拖拽：此时 GridView 正在重置集合，
        // 若进入拖拽会让 WinUI 在重建中又操作同一容器而崩溃。直接取消本次拖拽。
        if (ViewModel.Reloading)
        {
            e.Cancel = true;
            Log("拖拽被取消：全量刷新进行中，避免重建 ItemsSource 与拖拽冲突导致崩溃");
            return;
        }

        // 图片拖拽会话期间关闭分类列表重排（问题2 方案A）：防止拖拽图片经过分类栏时
        // 触发插入占位把分类列表撑开。不能在 DragOver 里才关——WinUI 在会话早期
        // 已按当时的 CanReorderItems=true 计算过占位，首次拖拽必然被撑开。
        CategoryList.CanReorderItems = false;

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
        if (ViewModel.EditMode && selected.Count > 0 && draggedVms.Any(v => selected.Contains(v)))
            group = selected;
        else
            group = draggedVms;

        ViewModel.DraggingMemes = group.Select(m => m.Model).ToList();
        // 锚点 = 实际拖起的那一张（e.Items[0]），用于重排时让它对齐鼠标落点。
        ViewModel.DragAnchorFileName = draggedVms.Count > 0 ? draggedVms[0].FileName : null;
        Log($"DragItemsStarting: 拖出 {ViewModel.DraggingMemes.Count} 张图片 (首项 {group[0].Title}, 锚点={ViewModel.DragAnchorFileName})");

        // 拖出格式按配置分支：
        //  - StorageFileDrag 关闭（默认，稳定优先）：仅用 SetBitmap + in-mem 流
        //    （进程内、同公寓，释放无跨公寓 COM 开销，安全）。单张任意类型（含 GIF）都设 Bitmap 流，
        //    老 QQ 认 Bitmap 格式（静态图可拖入；GIF 会变为静态图，已知代价）。
        //  - StorageFileDrag 开启（恢复文件拖出）：改用 SetDataProvider 延迟提供 StorageItems
        //    （+单张非 GIF 兜底 Bitmap）。拖放目标真正请求时才异步取 StorageFile，不在
        //    DragItemsStarting 同步构造跨公寓 COM 对象，从而避开 DataPackage 析构时的释放竞态
        //    (0x40080201 / 0xc000027b)。可让动态 GIF 等作为文件拖到 QQ，且不触发闪退。
        //    【注：此分支稳定性有待验证，验证期内若出问题可关掉 StorageFileDrag 退回稳定路径】
        // 内部重排(CanReorderItems)不读 DataPackage 内容，仅靠 Move 语义生效，互不影响。
        try
        {
            var valid = group
                .Where(v => !string.IsNullOrEmpty(v.LocalPath) && File.Exists(v.LocalPath))
                .ToList();
            if (valid.Count == 0)
            {
                Log("DragItemsStarting: 无可拖出文件（本地路径不存在），跳过设置拖出格式");
            }
            else if (ConfigService.Config.StorageFileDrag)
            {
                // 复用 ImageDragHelper：注册 StorageItems（延迟提供）+ 单张非 GIF 的 Bitmap 兜底。
                // GIF 仅作文件拖出（Bitmap 只静态图，塞 GIF 会变第一帧），保持动图。
                var paths = valid.Select(v => v.LocalPath!).ToArray();
                ImageDragHelper.ConfigureDragOut(e.Data, paths, true);
            }
            else
            {
                // 稳定路径：仅单张任意类型设 in-mem Bitmap 流（无 StorageFile）。
                if (valid.Count == 1)
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(valid[0].LocalPath!);
                        var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                        using (var dw = ms.GetOutputStreamAt(0))
                        using (var dwStream = dw.AsStreamForWrite())
                        {
                            dwStream.Write(bytes, 0, bytes.Length);
                            dwStream.Flush();
                        }
                        ms.Seek(0);
                        e.Data.SetBitmap(
                            Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(ms));
                    }
                    catch (Exception ex)
                    {
                        Log("DragItemsStarting: 构造位图流失败（已放弃图片格式）: " + ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("DragItemsStarting: 设拖出格式失败（已放弃，不影响重排）: " + ex.Message);
        }

        // 普通分类：Move 供内部重排(CanReorderItems)使用；Copy 供拖到外部(QQ/输入框)接收。
        // 全部表情：仅声明 Copy（禁止内部移动/重排，不允许 Move 语义）。
        // 仅声明 Copy 不会真的移走文件——外部按 Copy 取数据，文件仍留在数据目录。
        var op = IsAllMemesView
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move |
              Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.Data.RequestedOperation = op;
    }


    // 拖拽完成。编辑模式下 WinUI 内置重排已把 _memeList 真正重排好，这里读顺序写回 Priority。
    private async void MemeGridView_DragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
    {
        Log($"DragItemsCompleted: ViewModel.DraggingMemes={( ViewModel.DraggingMemes?.Count ?? 0 )}, ViewModel.EditMode={ViewModel.EditMode}, DropResult={e.DropResult}");
        // 拖拽会话结束（无论拖到哪/是否取消）：恢复分类列表重排能力
        // （DragItemsStarting 里为防插入占位撑开被临时关闭）。必须在所有提前 return 之前恢复。
        CategoryList.CanReorderItems = true;
        if (ViewModel.DraggingMemes == null) return;

        // “全部表情”视图下项来自不同分类，不存在单一分类顺序，禁止重排写回，
        // 避免跨分类顺序被错误地写到某个分类的 metadata；且内部拖拽本就被禁止，
        // 此处直接提前返回。拖到左侧分类栏移动归属走 CategoryList 的 Drop（MoveMemesToCategoryAsync），与此无关。
        if (IsAllMemesView)
        {
            Log("DragItemsCompleted: 当前为“全部表情”视图，跳过重排写回（跨分类不允许重排序）");
            // 清空拖拽态，否则 _draggingMemes 残留会导致 IsBusyBlockingInput 一直为真（F5 被挡等）
            ViewModel.DraggingMemes = null;
            ViewModel.DragAnchorFileName = null;
            return;
        }

        // 重排为写操作（会写回当前分类 metadata）：若已有用户主动发起的写任务在跑，
        // 直接放弃本次重排，避免并发写同一分类 .metadata.json 或顺序被收尾刷新冲掉。
        if (!TryGuardWrite()) return;

        // 拖拽结束：恢复预览定时器（仅当窗口可见）。与 DragItemsStarting 里的
        // _previewTimer.Stop() 成对，避免拖拽期间停定时器导致预览功能永久失效。
        if (App.MainWindow.IsAppVisible && !App.MainWindow.IsClosing)
            _previewTimer.Start();

        // 记录整组被拖项（编辑模式多选拖拽时是整组），重排后据此恢复多选状态
        var draggedGroup = ViewModel.DraggingMemes?.ToList() ?? new List<MemeModel>();

        // 用当前策略计算写回顺序：复用策略做“锚点对齐”，重建策略沿用 WinUI 默认顺序。
        var orderedFileNames = _listStrategy.ComputeDragOrder(ViewModel.MemeList, draggedGroup, ViewModel.DragAnchorFileName)
            ?? ViewModel.MemeList.Select(m => m.FileName).ToList();

        Log($"DragItemsCompleted: 重排完成, 项数={orderedFileNames.Count}");

        var ordered = orderedFileNames;

        try
        {
            await _engine.ReorderMemesAsync(ViewModel.CurrentCategory, ordered);
            Log($"DragItemsCompleted: 重排写回 {ordered.Count} 张图片到分类「{ViewModel.CurrentCategory}」");
        }
        catch (Exception ex)
        {
            Log($"[拖拽] ReorderMemesAsync 写回失败: {ex}");
        }
        // 场景A：仅顺序变、内容不变。已就地调整 _memeList，不重建集合以保持滚动条位置。

        // 编辑模式下：重排时 WinUI 会把选中重置为仅被拖动的那一张，导致多选变单选；
        // 这里按拖拽开始时记录的整组(_draggingMemes/draggedGroup)恢复多选高亮。
        if (ViewModel.EditMode && draggedGroup.Count > 0 && !App.MainWindow.IsClosing)
        {
            try
            {
                var vms = draggedGroup
                    .Select(m => ViewModel.MemeList.FirstOrDefault(v => v.FileName.Equals(m.FileName, StringComparison.OrdinalIgnoreCase)))
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

        // 图片被拖出数据目录(外部)导致文件消失，由 FileWatcher 监听并统一处理
        // （差集识别库内移动 vs 库外移出，就地移除失效控件+弹窗），此处无需额外逻辑。

        ViewModel.DraggingMemes = null;
        ViewModel.DragAnchorFileName = null;
    }

    private async void MemeGridView_Drop(object sender, DragEventArgs e)
    {
        Log("Drop 事件触发");
        var view = e.DataView;

        // 内部拖拽
        if (ViewModel.DraggingMemes != null && ViewModel.DraggingMemes.Count > 0)
        {
            var memes = ViewModel.DraggingMemes;
            ViewModel.DraggingMemes = null;

            // 编辑模式下，网格内重排交给 WinUI 内置重排（CanReorderItems），
            // 落点在 DragItemsCompleted 里读新顺序写回 Priority，这里不处理。
            if (ViewModel.EditMode)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                return;
            }

            // “全部表情”视图（CurrentCategory 为空）下：拖到网格自身无意义
            // （无单一当前分类可作移动目标），且禁止跨分类重排序。直接忽略，
            // 真正的“移动归属”请拖到左侧分类栏（CategoryListItem_Drop）。
            if (IsAllMemesView)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                return;
            }

            // 非编辑模式：拖到网格（当前分类）视为移动到当前分类（原地，通常无意义但保持行为一致）
            int moved = memes.Count(m => !m.Category.Equals(ViewModel.CurrentCategory, StringComparison.OrdinalIgnoreCase));
            if (moved > 0)
            {
                // 写锁守卫 + 冲突守卫 + 后台移动均委托 MemeOperationService
                await _memeOps.MoveMemesAsync(memes, ViewModel.CurrentCategory);
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                return;
            }
        }

        // 列出所有可用的数据格式，便于排查 QQ 等特殊来源
        var formats = view.AvailableFormats;
        Log($"Drop: 可用格式数量={formats.Count}");
        foreach (var f in formats)
            Log($"Drop: 格式 = {f}");

        // 收集所有待导入的源路径（复用 ImageDragHelper：StorageItems 直接用原路径；Bitmap 先落临时文件）
        var importPaths = await ImageDragHelper.CollectDropPathsAsync(view);
        // 标记临时文件（Bitmap 落地），导入后用于清理
        var tempPrefix = System.IO.Path.GetTempPath().TrimEnd('\\').ToLowerInvariant();
        var tempPaths = importPaths
            .Where(p => p.ToLowerInvariant().StartsWith(tempPrefix))
            .ToList();

        try
        {
            // 统一走后台批量导入（含写入锁守卫、进度条、分类守卫刷新）
            if (importPaths.Count > 0)
                await RunBatchImportAsync(importPaths, ImportTargetCategory);
        }
        finally
        {
            // 清理 Bitmap 落地的临时文件（导入已在后台读取完毕，此时删除安全）
            foreach (var t in tempPaths)
            {
                try { System.IO.File.Delete(t); } catch { }
            }
        }

        if (importPaths.Count == 0)
            Log("拖入: 未导入任何图片（已忽略不符合要求的拖拽对象）");
    }

    // ---------- Win32 层拖入文件（WM_DROPFILES，由 MainWindow 转发路径）----------

    public void HandleExternalDropPaths(System.Collections.Generic.List<string> paths)
    {
        // 拦截拖入：写任务进行中弹“操作进行中”提示（与其它写入口一致）；
        // 其余忙态（模态弹窗未处理 / 拖拽重排中 / 刷新中）仅静默拦截，避免叠加弹窗。
        if (IsBusyBlockingInput())
        {
            if (_batchRunner.IsWriteActive)
                _ = DialogHelper.ShowWriteBusyAsync(this.XamlRoot);
            else
                Log("WM_DROPFILES 被拦截：存在未处理的模态弹窗 / 拖拽重排 / 刷新进行中，忽略本次拖入");
            return;
        }

        if (paths.Count > 0)
            Log($"拖入 {paths.Count} 个文件, 目标分类={ViewModel.CurrentCategory}");

        // 异步导入，不阻塞窗口过程
        _ = ImportDroppedFilesAsync(paths);
    }

    private async Task ImportDroppedFilesAsync(System.Collections.Generic.List<string> paths)
    {
        if (paths.Count == 0) return;

        // 复用后台批量导入（含进度条与分类守卫），拖入与按钮导入行为保持一致
        await RunBatchImportAsync(paths, ImportTargetCategory);
    }

    // ---------- 右键菜单（XAML ContextFlyout 绑定）----------

    // 当前右键所操作的表情（由 ContextFlyout.Opening 写入，供各 Click 使用）

    // 表情右键菜单打开时：记录当前表情，并动态填充“移动到其他分类”子菜单
    private void MemeItemContextFlyout_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout || flyout.Target is not FrameworkElement fe)
            return;
        if (fe.DataContext is not MemeViewModel vm)
            return;

        Log("右键单击表情项: " + vm.Title);

        // 动态子菜单：列出除当前分类外的所有分类
        // 注意：DataTemplate 内的 x:Name 不会提升为页面字段，这里从 flyout 里按类型/文本查找。
        var moveSub = flyout.Items
            .OfType<MenuFlyoutSubItem>()
            .FirstOrDefault(s => s.Text == Localization.Get("Meme_MoveToOtherCategory"));
        if (moveSub == null) return;

        moveSub.Items.Clear();

        // “全部表情”视图下项来自不同分类，禁止移动归属：子菜单保留可点，
        // 点击弹出模态提示说明不允许。
        if (IsAllMemesView)
        {
            var blockedItem = new MenuFlyoutItem { Text = Localization.Get("AllMemes_MoveDisabledTip") };
            blockedItem.Click += async (_, __) =>
                await DialogHelper.ShowInfoAsync(this.XamlRoot,
                    Localization.Get("AllMemes_MoveBlockedTitle"),
                    Localization.Get("AllMemes_MoveBlockedMessage"));
            moveSub.Items.Add(blockedItem);
            moveSub.IsEnabled = true;
            return;
        }

        bool hasTarget = false;
        foreach (var cat in ViewModel.CategoryList)
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

    // 删除单张表情（由 VM DeleteMemeCommand 经事件转发；含确认弹窗 + 写锁 + 后台删除）
    private async Task DeleteMemeCoreAsync(MemeViewModel vm)
        => await _memeOps.DeleteMemeAsync(vm.Model);

    // IMemeOperationUi：单张删除确认弹窗（服务不引用 XamlRoot，经此回调甩回 Page）。
    public Task<bool> ConfirmDeleteMemeAsync(string title) =>
        DialogHelper.ConfirmDeleteMemeAsync(this.XamlRoot, title).ContinueWith(t => t.Result == ContentDialogResult.Primary, TaskScheduler.Default);

    // 移动表情到其他分类（编辑模式且有选中项则移动所有选中项，否则只移动当前项）
    private async void MoveMemeToCategory(MemeViewModel vm, string targetName)
    {
        List<MemeViewModel> toMove;
        var selected = MemeGridView.SelectedItems.Cast<MemeViewModel>().ToList();
        if (ViewModel.EditMode && selected.Count > 0)
            toMove = selected;
        else
            toMove = new List<MemeViewModel> { vm };

        // 写锁守卫 + 冲突守卫 + 后台移动均委托 MemeOperationService（含 GuardMoveConflictAsync）。
        await _memeOps.MoveMemesAsync(toMove.Select(m => m.Model), targetName);
    }

    // IMemeOperationUi：批量删除确认弹窗。
    public Task<bool> ConfirmDeleteMemesAsync(int count) =>
        DialogHelper.ConfirmDeleteMemesAsync(this.XamlRoot, count).ContinueWith(t => t.Result == ContentDialogResult.Primary, TaskScheduler.Default);

    // IMemeOperationUi：移动冲突弹窗。
    public Task ShowMoveConflictAsync(string targetCategory, IEnumerable<(string src, string dst)> pairs) =>
        DialogHelper.ShowMoveConflictAsync(this.XamlRoot, targetCategory, pairs);

    // IMemeOperationUi：删除完成后清空网格选中态。
    public void OnDeleteComplete() => MemeGridView.SelectedItems.Clear();

    // 注：ShowWriteBusyAsync 已在 IImportExportUi 实现处提供（两接口签名相同，单一实现即可满足）。

    // ---------- 批量操作 ----------

    private async Task BatchImportCoreAsync()
    {
        var files = await PickerHelper.PickMultipleFilesAsync(
            App.MainWindow,
            PickerLocationId.PicturesLibrary,
            (Localization.Get("Meme_ImageFileType"), ".png"), (Localization.Get("Meme_ImageFileType"), ".jpg"), (Localization.Get("Meme_ImageFileType"), ".jpeg"),
            (Localization.Get("Meme_ImageFileType"), ".gif"), (Localization.Get("Meme_ImageFileType"), ".webp"), (Localization.Get("Meme_ImageFileType"), ".bmp"));

        if (files.Count == 0) return;

        await RunBatchImportAsync(files, ImportTargetCategory);
    }

    // 写操作入口守卫：若已有用户主动发起的写任务（导入/移动/删除）在跑，
    // 弹出“操作进行中”模态提示并放弃本次操作；否则返回 true 放行。
    // 导出（copy 语义）与文件监听触发的导入不走此守卫。
    private bool TryGuardWrite()
        => _importExport.TryGuardWrite();

    // IImportExportUi：写锁忙提示（服务不引用 XamlRoot，经此回调甩回 Page）。
    public Task ShowWriteBusyAsync() => DialogHelper.ShowWriteBusyAsync(this.XamlRoot);

    // 是否应拦截用户输入（拖入/F5 等）：有任意模态弹窗未处理、已有写任务在跑，
    // 或网格正处于拖拽重排中（拖拽中重建 ItemsSource 会令 WinUI 崩溃）。
    // 模态窗优先（避免叠加弹窗），写锁与拖拽次之。
    private bool IsBusyBlockingInput() =>
        DialogHelper.IsModalOpen || _batchRunner.IsWriteActive || ViewModel.DraggingMemes != null || ViewModel.Reloading;

    // 后台批量导入：委托 ImportExportService（实际编排仍走 _batchRunner）。
    // 导入过程中新建分类时，经 onCategoryCreated 回调把新分类加入左侧栏（ViewModel 状态）。
    private async Task RunBatchImportAsync(IEnumerable<string> files, string category)
    {
        await _importExport.RunBatchImportAsync(files, category, onCategoryCreated: createdName =>
        {
            if (!ViewModel.CategoryList.Any(c => c.Name.Equals(createdName, StringComparison.OrdinalIgnoreCase)))
                ViewModel.CategoryList.Add(new CategoryViewModel(createdName, 0));
        });
    }

    // IImportExportUi：单张导入重复的提示弹窗（服务不引用 XamlRoot，经此回调甩回 Page）。
    public Task ShowSingleImportDuplicateAsync(MemeModel existing) =>
        DialogHelper.ShowImageDuplicateAsync(
            this.XamlRoot,
            ImportTargetCategory,
            DialogHelper.TruncateLabel(string.IsNullOrWhiteSpace(existing.Title) ? existing.FileName : existing.Title));

    // 当前 GridView 原生选中的项
    private List<MemeViewModel> SelectedMemeViewModels()
        => MemeGridView.SelectedItems.Cast<MemeViewModel>().ToList();

    private async Task BatchExportCoreAsync()
    {
        var selected = SelectedMemeViewModels();
        if (selected.Count == 0) return;

        var folder = await PickerHelper.PickFolderAsync(App.MainWindow);
        if (folder == null) return;

        var models = selected.Select(m => m.Model).ToList();
        if (models.Count == 0) return;

        // 批量导出按钮：明确为 copy 语义，委托 ImportExportService（不占用写入锁）。
        await _importExport.BatchExportCoreAsync(models, folder);
    }

    // 删除当前选中的表情：复用删除按钮的确认弹窗 + 写锁守卫 + 后台删除流程。
    // 编辑模式下由删除快捷键调用；若进入编辑模式但未选中任何图片则不响应。
    private async Task DeleteSelectedMemesAsync()
    {
        var selected = SelectedMemeViewModels();
        if (selected.Count == 0) return;
        await _memeOps.DeleteMemesAsync(selected.Select(m => m.Model).ToList());
    }

    // 批量移动到：弹出分类下拉，点击后将选中项移动到该分类
    private void BatchMoveFlyout_Opening(object? sender, object e)
    {
        if (BatchMoveFlyout == null) return;
        BatchMoveFlyout.Items.Clear();

        var selected = SelectedMemeViewModels();
        if (selected.Count == 0) return;

        // “全部表情”视图下禁止移动归属：菜单保留可点，点击弹出模态提示说明不允许。
        if (IsAllMemesView)
        {
            var blockedItem = new MenuFlyoutItem { Text = Localization.Get("AllMemes_MoveDisabledTip") };
            blockedItem.Click += async (_, __) =>
                await DialogHelper.ShowInfoAsync(this.XamlRoot,
                    Localization.Get("AllMemes_MoveBlockedTitle"),
                    Localization.Get("AllMemes_MoveBlockedMessage"));
            BatchMoveFlyout.Items.Add(blockedItem);
            return;
        }

        bool hasTarget = false;
        foreach (var cat in ViewModel.CategoryList)
        {
            // 跳过当前所在分类（移动过去无意义）
            if (ViewModel.CurrentCategory.Equals(cat.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            hasTarget = true;
            var targetName = cat.Name;
            var item = new MenuFlyoutItem { Text = cat.Name };
            item.Click += async (_, __) =>
            {
                var models = selected.Select(m => m.Model).ToList();
                // 写锁守卫 + 冲突守卫 + 后台移动均委托 MemeOperationService
                await _memeOps.MoveMemesAsync(models, targetName);
            };
            BatchMoveFlyout.Items.Add(item);
        }

        if (!hasTarget)
        {
            BatchMoveFlyout.Items.Add(new MenuFlyoutItem { Text = Localization.Get("Meme_NoOtherCategory"), IsEnabled = false });
        }
    }

    private void ShowSettingsFlyout()
    {
        var page = new SettingsPage();
        page.RequestClose += (_, _) => SettingsFlyout.Hide();
        SettingsFlyout.Content = page;
        SettingsFlyout.ShowAt(SettingsButton);
    }

    // 托盘菜单"设置"入口（由 MainWindow 转发）：直接弹出设置浮窗
    public void OpenSettingsFlyout()
    {
        ShowSettingsFlyout();
    }

    // 切换到 Mini 模式（仅当配置允许时，按钮本身也会隐藏）
    private void SwitchToMiniMode()
    {
        if (!ConfigService.Config.AllowMiniMode)
            return;
        App.MainWindow.SwitchMode(AppMode.Mini);
    }

    // 依据 config 的 AllowMiniMode 控制 Mini 按钮可见性（设置页改动后刷新）。
    public void ApplyMiniModeVisibilityFromConfig()
    {
        if (MiniModeButton != null)
            MiniModeButton.Visibility = ConfigService.Config.AllowMiniMode
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>重新读取数据目录并重渲染：分类、表情、缩略图全部刷新</summary>
    private async Task RefreshDataAsync()
    {
        // 拖拽重排进行中刷新会令 WinUI 崩溃（重建 ItemsSource），写任务进行中重载会与写操作抢数据，
        // 有模态窗时也不应插入刷新，重载自身也不得重入。统一用 IsBusyBlockingInput 拦截。
        if (IsBusyBlockingInput() || ViewModel.Reloading)
        {
            if (_batchRunner.IsWriteActive)
                _ = DialogHelper.ShowWriteBusyAsync(this.XamlRoot);
            else if (ViewModel.DraggingMemes != null)
                Log("刷新被忽略：网格拖拽重排进行中，避免重建 ItemsSource 导致崩溃");
            else if (ViewModel.Reloading)
                Log("刷新被忽略：已有刷新在进行中");
            else
                Log("刷新被忽略：存在未处理的模态弹窗");
            return;
        }

        ViewModel.Reloading = true;
        try
        {
            Log("刷新：重新读取数据目录");
            await _engine.InitializeAsync();
            LoadCategories(restoreSelectionFromConfig: false); // 刷新时以内存当前分类为准，不被旧 config 覆盖
        }
        finally
        {
            ViewModel.Reloading = false;
        }
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
                // 刷新场景：以内存当前分类为准（不被尚未 flush 的旧 config 覆盖）。
                LoadCategories(restoreSelectionFromConfig: false);
            }
        }
    }

    // 置顶开关：用户手动切换窗口置顶状态（仅会话内有效，不持久化到 config）
    private void TopMostToggle_Checked(object sender, RoutedEventArgs e)
    {
        App.MainWindow.SetTopMost(true);
        Log("[置顶] 已开启置顶");
    }

    private void TopMostToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        App.MainWindow.SetTopMost(false);
        Log("[置顶] 已关闭置顶");
    }

    // 搜索框输入防抖：避免每次按键都重建表情列表

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.SearchDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        ViewModel.SearchDebounceTimer.Stop();
        ViewModel.SearchDebounceTimer.Tick -= SearchDebounce_Tick;
        ViewModel.SearchDebounceTimer.Tick += SearchDebounce_Tick;
        ViewModel.SearchDebounceTimer.Start();
    }

    private void SearchDebounce_Tick(object? sender, object e)
    {
        ViewModel.SearchDebounceTimer?.Stop();
        RefreshMemes();
    }

    public void HandleHostKeyDown(KeyRoutedEventArgs e) => Root_KeyDown(null, e);

    private async void Root_KeyDown(object? sender, KeyRoutedEventArgs e)
    {
        // Ctrl+V：仅在本窗口激活（焦点在主窗口）时，才把剪贴板里的图片导入到分类。
        // 这样截图等写剪贴板的行为不会误触发“粘贴到分类”；无焦点时的 Ctrl+V 仍走投回外部逻辑。
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == Windows.System.VirtualKey.V)
        {
            if (!App.MainWindow.IsWindowActive)
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

        // Ctrl+A：编辑模式下全选/取消全选；非编辑模式下自动进编辑模式并全选
        if (ctrl && e.Key == Windows.System.VirtualKey.A)
        {
            if (!ViewModel.EditMode) EnterEditModeAndSelectAll();
            else ToggleSelectAll();
            e.Handled = true;
            return;
        }

        // Ctrl+E：切换编辑（多选）模式，与“修改”按钮语义一致
        if (ctrl && e.Key == Windows.System.VirtualKey.E)
        {
            ToggleEditMode();
            e.Handled = true;
            return;
        }

        // F2：重命名当前选中的分类（聚焦分类控件时）
        if (e.Key == Windows.System.VirtualKey.F2)
        {
            if (CategoryList.SelectedItem is CategoryViewModel selCat)
            {
                e.Handled = true;
                await ViewModel.RenameCategoryCommand.ExecuteAsync(selCat);
            }
            return;
        }

        // Delete：编辑模式下删除已选中的表情（复用删除按钮的确认弹窗与流程）；
        // 若编辑模式但未选中任何图片则不响应。非编辑模式下仍按原逻辑删除选中的分类。
        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            if (ViewModel.EditMode)
            {
                if (SelectedMemeViewModels().Count > 0)
                {
                    e.Handled = true;
                    await DeleteSelectedMemesAsync();
                }
                // 编辑模式但无选中：不响应（也不删除分类）
                return;
            }

            if (CategoryList.SelectedItem is CategoryViewModel selCat)
            {
                e.Handled = true;
                await ViewModel.DeleteCategoryCommand.ExecuteAsync(selCat);
            }
            return;
        }

        if (!ViewModel.EditMode) return;

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
        if (!ViewModel.EditMode) return;
        bool allSelected = MemeGridView.SelectedItems.Count == ViewModel.MemeList.Count && ViewModel.MemeList.Count > 0;
        if (allSelected)
            MemeGridView.SelectedItems.Clear();
        else
            // 用 GridView 内置批量选中，避免逐项 Add 触发 O(n²) 判断 + 重复 UI 刷新导致卡顿
            MemeGridView.SelectAll();
        UpdateSelectAllButton();
    }

    // 按当前实际选中态更新“全选/取消全选”按钮文案，避免在 ToggleSelectAll 里用操作前的状态导致文字反置，
    // 也覆盖 Ctrl+A 原生、鼠标框选等其它改变选中的路径。
    private void UpdateSelectAllButton()
    {
        if (SelectAllButton == null) return;
        bool allSelected = ViewModel.EditMode && MemeGridView.SelectedItems.Count == ViewModel.MemeList.Count && ViewModel.MemeList.Count > 0;
        SelectAllButton.Content = allSelected ? Localization.Get("Meme_CancelSelectAll") : Localization.Get("Meme_SelectAll");
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
                Log("[粘贴] 剪贴板非图片/图片路径类内容");
                await DialogHelper.ShowClipboardNotImageAsync(this.XamlRoot);
                return;
            }

            var category = await PromptCategoryForPasteAsync();
            if (category == null) return;

            var (paths, temps) = await CollectClipboardImportPathsAsync(view);
            try
            {
                if (paths.Count == 0)
                {
                    Log("[粘贴] 导入失败或内容为空");
                    return;
                }
                // 统一走后台批量导入（含写入锁守卫、进度条、分类守卫刷新、单张重复弹窗）
                await RunBatchImportAsync(paths, category);
            }
            finally
            {
                foreach (var t in temps)
                {
                    try { File.Delete(t); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log("[粘贴] PasteFromClipboardViaShortcutAsync 失败: " + ex.Message);
        }
    }

    internal void ExitEditMode()
    {
        // 幂等：非编辑模式下直接返回，供 MainWindow.SwitchMode 等新调用方安全调用。
        if (!ViewModel.EditMode) return;
        ViewModel.EditMode = false;
        EditButton.Content = Localization.Get("Meme_Edit");
        BatchBar.Visibility = Visibility.Collapsed;
        // 退出编辑模式：仅关闭原生多选；拖拽重排保持开启（普通模式也允许排序）
        MemeGridView.SelectedItems.Clear();
        MemeGridView.SelectionMode = ListViewSelectionMode.None;
        ViewModel.LastShiftAnchor = -1;
        SelectAllButton.Content = Localization.Get("Meme_SelectAll");
        ApplySelectionBoxVisibility();
        foreach (var vm in ViewModel.MemeList) vm.IsSelected = false;
    }

    // 编辑模式进出时，统一切换所有 item 容器内复选框的可见性。
    // 复选框本身 IsHitTestVisible=False，仅作选中指示，不拦截 Tapped/拖拽。
    // 由于 GridView 虚拟化，容器可能尚未实现，故在下一帧(Dispatcher)再遍历，
    // 并通过可视化树查找 CheckBox（DataTemplate 内 x:Name 不会提升为页面字段）。
    // 统一入口：仅当处于编辑模式且开启"资源管理器风格多选"时才显示自绘右上角复选框。
    // 所有切换入口（进/退编辑、列表刷新、设置变更）都经此，避免状态不一致导致的
    // 复选框消失/残留（#23）。
    private void ApplySelectionBoxVisibility()
    {
        bool show = ViewModel.EditMode && ConfigService.Config.ExplorerStyleMultiSelect;
        SetSelectionBoxVisible(show);
    }

    private void SetSelectionBoxVisible(bool visible)
    {
        if (App.MainWindow.IsClosing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (App.MainWindow.IsClosing) return;
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
        if (ViewModel.PasteDialogOpen)
        {
            Log("[剪贴板] 分类对话框重入，跳过");
            return null;
        }
        ViewModel.PasteDialogOpen = true;

        try
        {
            var name = await DialogHelper.PromptPasteCategoryAsync(this.XamlRoot, ImportTargetCategory);
            if (string.IsNullOrWhiteSpace(name))
            {
                Log("[剪贴板] 取消粘贴");
                return null;
            }

            // 分类名严格校验（拒绝 "."/".."、路径分隔符、非法字符等，见 FileNameValidator.IsValidCategoryName）。
            // 安全审计 Critical：此前零校验，输入 ".." 会经 Path.Combine(_baseDir, "..") 越界写/删父目录。
            if (!FileNameValidator.IsValidCategoryName(name))
            {
                Log($"[剪贴板] 分类名非法，已取消粘贴: {name}");
                await DialogHelper.ShowInvalidCategoryNameAsync(this.XamlRoot, name);
                return null;
            }

            if (!ViewModel.CategoryList.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                // 仅在引擎真正创建成功后才加入 UI 列表；失败（如目录已存在）则取消本次粘贴，
                // 避免 UI 出现指向错误目录的分类项。
                bool added = await _categories.AddCategoryAsync(name);
                if (!added)
                {
                    Log($"[剪贴板] 新建分类失败，已取消粘贴: {name}");
                    return null;
                }
                ViewModel.CategoryList.Add(new CategoryViewModel(name, 0));
                Log($"[剪贴板] 新建分类 {name}");
            }
            return name;
        }
        finally
        {
            ViewModel.PasteDialogOpen = false;
        }
    }

    // 从剪贴板收集待导入的源路径：StorageItems 直接用原路径，Bitmap 先落地临时文件。
    // 返回 (待导入路径, 需事后清理的临时文件路径)。实际导入交给 RunBatchImportAsync 统一处理。
    private async Task<(List<string> paths, List<string> temps)> CollectClipboardImportPathsAsync(DataPackageView view)
    {
        var paths = new List<string>();
        var temps = new List<string>();
        try
        {
            if (view.Contains(StandardDataFormats.StorageItems))
            {
                var items = await view.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is StorageFile file && IsImage(file.FileType))
                        paths.Add(file.Path);
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
                paths.Add(tempPath);
                temps.Add(tempPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Paste] 收集路径失败: {ex.Message}");
        }
        return (paths, temps);
    }

    internal static bool IsImage(string ext) => AppConstants.IsImage(ext);

    // ---------- 窗口交互挂起/恢复（由 MainWindow 在隐藏/显示/销毁时调用） ----------

    // 窗口即将隐藏/销毁时调用：停掉 WinUI 拖拽能力，防止拖拽会话进行中或
    // 隐藏期间这些回调触发 XAML 操作导致 native AV(0xc0000005)。
    public void SuspendInteractions()
    {
        // 立即停止预览浮窗（不淡出），避免隐藏/销毁期间其异步回调访问已卸载的可视化树
        HidePreviewPopup(immediate: true, "SuspendInteractions");

        // 停止 WinUI 内置拖拽/重排，让进行中的拖拽会话安全结束
        MemeGridView.CanDragItems = false;
        MemeGridView.CanReorderItems = false;
        MemeGridView.AllowDrop = false;
        CategoryList.CanDragItems = false;
        CategoryList.CanReorderItems = false;
        CategoryList.AllowDrop = false;
    }

    // 窗口重新显示时调用：恢复拖拽能力
    public void ResumeInteractions()
    {
        if (App.MainWindow.IsClosing) return;
        Log($"[防护] ResumeInteractions: ViewModel.EditMode={ViewModel.EditMode}");

        // 恢复 WinUI 拖拽能力：拖出(CanDragItems)与拖拽重排(CanReorderItems)在
        // 普通模式和编辑模式都需要（普通模式也能在窗口内拖动排序并落库）。
        CategoryList.CanReorderItems = true;
        // 注意：MemeGridView.CanReorderItems 不在此处无条件开启——下方 SyncMemeDragState()
        // 会根据 IsAllMemesView 正确设置（全部表情视图下保持 False），避免覆盖重排限制。
        MemeGridView.AllowDrop = true;
        CategoryList.CanDragItems = true;
        CategoryList.AllowDrop = true;
        // 拖拽能力跟随当前视图：全部表情视图下网格禁止拖出（见 SyncMemeDragState）
        SyncMemeDragState();

        Log("[防护] 已恢复窗口交互");
    }

    // ---------- 数据目录文件监听 ----------

    // 引擎层 FileWatcher 探测到图片文件从库中消失（外部拖出/被删）后回调。
    // 事件可能在非 UI 线程触发，统一回 UI 线程处理。
    // 仅当变化的分类 == 当前焦点分类时才改控件；其他分类的控件不在内存里，
    // 跳过即可，下次切到该分类时 RefreshMemes 自然反映。
    private void OnWatchedFilesRemoved(IReadOnlyList<FileWatcher.Change> changes)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (App.MainWindow.IsClosing) return;
            var focus = ViewModel.CurrentCategory;
            var names = changes
                .Where(c => string.Equals(c.Category, focus, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.FileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Count == 0) return; // 非焦点分类，跳过

            var toRemove = ViewModel.MemeList
                .Where(vm => !string.IsNullOrEmpty(vm.LocalPath) && names.Contains(Path.GetFileName(vm.LocalPath)))
                .ToList();
            if (toRemove.Count == 0) return;

            Log($"[文件监听] 移除 {toRemove.Count} 个已从库消失的图片控件 (分类={focus})");
            var models = toRemove.Select(vm => vm.Model).ToList();
            _engine.RemoveMemesFromCache(models);
            RemoveFromCurrentView(models);
            UpdateCategoryCounts();
            await DialogHelper.ShowImageMovedOutAsync(this.XamlRoot);
        });
    }

    // 引擎层 FileWatcher 探测到图片文件新增（手动往分类文件夹塞图等兜底）后回调。
    // 仅当新增分类 == 当前焦点分类时才就地追加控件；否则跳过（切到该分类时自会刷新）。
    private void OnWatchedFilesAdded(IReadOnlyList<FileWatcher.Change> changes)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (App.MainWindow.IsClosing) return;
            var focus = ViewModel.CurrentCategory;
            var added = changes
                .Where(c => string.Equals(c.Category, focus, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (added.Count == 0) return; // 非焦点分类，跳过

            // 收集真实存在的文件全路径（过滤已存在于列表的，避免冗余导入）
            var fullPaths = added
                .Where(c => !ViewModel.MemeList.Any(vm => !string.IsNullOrEmpty(vm.LocalPath) &&
                    string.Equals(Path.GetFileName(vm.LocalPath), c.FileName, StringComparison.OrdinalIgnoreCase)))
                .Select(c => Path.Combine(_engine.BaseDir, c.Category, c.FileName))
                .Where(File.Exists)
                .ToList();
            if (fullPaths.Count > 0)
            {
                // 文件监听触发的新增：后台执行 + 进度条，但不占用写入锁
                // （用户自行在资源管理器操作数据目录，应自己 F5 刷新，不在此兜底拦截）。
                // 走批量导入：目标分类 .metadata.json 仅读写一次，避免 N 张 = O(N^2) IO。
                await _batchRunner.RunAsync(
                    BatchOperationKind.Import,
                    fullPaths.Count,
                    async progress =>
                    {
                        await _engine.ImportMemesAsync(fullPaths, focus, progress);
                    },
                    targetCategory: focus,
                    occupyWriteLock: false,
                    onUiComplete: () => Log($"[文件监听] 新增 {fullPaths.Count} 个图片到分类「{focus}」"));
            }
        });
    }

    // 引擎层 FileWatcher 探测到图片在分类间移动（如手动在资源管理器里拖动文件）后回调。
    // 仅当移动涉及当前焦点分类时才就地更新：源为焦点则移除、目标为焦点则追加。
    // 不弹窗（与软件内移动/导入行为一致），切到其他分类时自会刷新。
    private void OnWatchedFilesMoved(IReadOnlyList<FileWatcher.Move> moves)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (App.MainWindow.IsClosing) return;
            var focus = ViewModel.CurrentCategory;

            // 源为焦点分类：移除对应控件
            var fromNames = moves
                .Where(m => string.Equals(m.From.Category, focus, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.From.FileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (fromNames.Count > 0)
            {
                var toRemove = ViewModel.MemeList
                    .Where(vm => !string.IsNullOrEmpty(vm.LocalPath) && fromNames.Contains(Path.GetFileName(vm.LocalPath)))
                    .ToList();
                if (toRemove.Count > 0)
                {
                    RemoveFromCurrentView(toRemove.Select(vm => vm.Model));
                    UpdateCategoryCounts();
                    Log($"[文件监听] 移出 {toRemove.Count} 个图片 (分类={focus})");
                }
            }

            // 目标为焦点分类：追加对应控件（复用导入流程，ImportMemeAsync 会去重）
            var toAdded = moves
                .Where(m => string.Equals(m.To.Category, focus, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (toAdded.Count > 0)
            {
                // 收集真实存在的文件全路径（过滤不存在的）
                var fullPaths = toAdded
                    .Select(m => Path.Combine(_engine.BaseDir, m.To.Category, m.To.FileName))
                    .Where(File.Exists)
                    .ToList();
                if (fullPaths.Count > 0)
                {
                    // 文件监听触发的导入：后台执行 + 进度条，但不占用写入锁
                    // （用户自行在资源管理器操作数据目录，应自己 F5 刷新，不在此兜底拦截）。
                    // 走批量导入：目标分类 .metadata.json 仅读写一次，避免 O(N^2) IO。
                    await _batchRunner.RunAsync(
                        BatchOperationKind.Import,
                        fullPaths.Count,
                        async progress =>
                        {
                            await _engine.ImportMemesAsync(fullPaths, focus, progress);
                        },
                        targetCategory: focus,
                        occupyWriteLock: false,
                        onUiComplete: () => Log($"[文件监听] 移入 {fullPaths.Count} 个图片到分类「{focus}」"));
                }
            }
        });
    }
}
