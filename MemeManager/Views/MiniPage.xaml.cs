using MemeManager.Infrastructure;
using MemeManager.Models;
using MemeManager.Services;
using MemeManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace MemeManager.Views;

public sealed partial class MiniPage : Page, IExternalDropPage, IImageReleasablePage
{
    // Picker 打开时才按需加载的缩略图列表（避免后台常驻解码）。
    private List<MemeViewModel> _pickerMemes = [];

    private readonly ConfigService ConfigService = App.GetService<ConfigService>();

    // Mini 模式当前选中的分类；虚拟分类名（如 "AllMemes"）/ 普通分类名（文件夹名）。
    private string _currentCategory = CategoryKind.All.VirtualName();

    // 是否处于“全部表情”视图（导入落到未分类，与 Full 一致）。
    private bool IsAllMemesView => _currentCategory == CategoryKind.All.VirtualName();

    // 拖入/导入时的目标分类：全部表情视图落入“未分类”，否则按当前分类（复用 Full 规则）。
    private string ImportTargetCategory =>
        IsAllMemesView ? AppConstants.UncategorizedCategory : _currentCategory;

    // 用于取消上一次“提示文字自动恢复”的定时任务，避免多条消息互相抢占。
    private CancellationTokenSource? _hintRestoreCts;

    // 导入成功提示自动恢复为默认文案的延迟时长（毫秒）。
    private const int ImportHintRestoreDelay = 7 * 1000;

    private readonly MemeDataEngine _engine =
        App.GetService<MemeDataEngine>();

    private readonly ClipboardService _clipboard = App.GetService<ClipboardService>();

    public MiniViewModel ViewModel => (MiniViewModel)DataContext;

    public MiniPage()
    {
        InitializeComponent();
        DataContext = App.GetService<MiniViewModel>();
        // 单例 VM + 每次重建 Page：用 '=' 赋值（非 +=）覆盖式接管，VM 上永远只有 1 份，
        // 旧 Page 失引用即被 GC，天然不累积（根除"Mini 切几次后 Expand 按钮失效"的 R3 泄漏）。
        ViewModel.ExpandToFullRequested = () => App.MainWindow.SwitchMode(AppMode.Full);
        ViewModel.SendToExternalRequested = async vm =>
        {
            var target = App.MainWindow.ResolveExternalPasteTarget();
            if (target == IntPtr.Zero)
            {
                Logger.Log("[Mini] 点击发送：未解析到有效外部窗口，取消本次粘贴");
                return;
            }
            Logger.Log($"[Mini] 点击发送图片 ({Path.GetFileName(vm.LocalPath)}) -> 目标={target:X}");
            await _clipboard.OutputMemeToCursorAsync(vm.LocalPath, target);
        };
        Loaded += MiniPage_Loaded;
        // 页面销毁时显式断开 x:Bind 绑定引用，兜底 Reference Tracker 清理。
        Unloaded += MiniPage_Unloaded;

        SaveLastCategoryDebouncer = new(AppConstants.LastCategorySaveDebounce, async (category) =>
        {
            await SaveCurrentCategoryToConfig(category);
            return;
        });
    }

    private void MiniPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= MiniPage_Unloaded;
        Bindings?.StopTracking();
    }

    private void MiniPage_Loaded(object sender, RoutedEventArgs e)
    {
        LoadCategories();
        // 把顶栏注册为自定义标题栏：系统据此让该区域可拖，且内部按钮仍可点击。
        App.MainWindow.SetTitleBarElement(DragBar);
    }

    /// <summary>将焦点设置到提示文字区域（供窗口从托盘呼出后调用，避免焦点残留在系统关闭按钮上）</summary>
    public void FocusDropHint()
    {
        CategoryCombo.Focus(FocusState.Programmatic);
    }


    // ---------- 分类下拉 ----------

    private void LoadCategories()
    {
        var cats = _engine.GetCategories();
        if (cats.Count == 0)
            _engine.EnsureDefaultCategory();
        cats = _engine.GetCategories();

        // 复用 Full 的 CategoryViewModel：头部插入“全部表情”虚拟项（Name 空串），
        // 与 Full 的 _allMemesVm 约定一致（空名 = 全部表情）。
        var items = new List<CategoryViewModel>
        {
            new("", 0)
        };
        foreach (var c in cats)
            items.Add(new CategoryViewModel(c, _engine.GetMemes(c).Count));
        CategoryCombo.ItemsSource = items;

        var (lastKind, lastName) = ConfigService.Config.LastCategory.Resolve();
        if (lastKind != CategoryKind.Normal)
        {
            // 上次停留在虚拟分类（如“全部表情”）：选中头部虚拟项。
            CategoryCombo.SelectedIndex = 0;
            _currentCategory = CategoryKind.All.VirtualName();
        }
        else
        {
            var idx = items.FindIndex(v => !string.IsNullOrEmpty(v.Name)
                && v.Name.Equals(lastName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                CategoryCombo.SelectedIndex = idx;
                _currentCategory = items[idx].Name;
            }
            else
            {
                CategoryCombo.SelectedIndex = 0;
                _currentCategory = CategoryKind.All.VirtualName();
            }
        }
    }

    private readonly AsyncDebouncer<string> SaveLastCategoryDebouncer;

    private async Task SaveCurrentCategoryToConfig(string categoryName)
    {
        await ConfigService.UpdateConfigAsync(cfg => cfg.LastCategory = categoryName);
    }

    internal async Task SaveCurrentCategoryToConfigWhenExit()
    {
        await SaveCurrentCategoryToConfig(_currentCategory);
    }

    private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryCombo.SelectedItem is ViewModels.CategoryViewModel vm)
        {
            // 空名 = “全部表情”视图（与 Full 约定一致）。
            if (string.IsNullOrEmpty(vm.Name))
            {
                var categoryName = CategoryKind.All.VirtualName();
                _currentCategory = categoryName;
                SaveLastCategoryDebouncer.Trigger(categoryName);
            }
            else
            {
                var categoryName = vm.Name;
                _currentCategory = categoryName;
                SaveLastCategoryDebouncer.Trigger(categoryName);
            }
            ReleaseImages(detachItemsSource: true);
        }
    }

    // ---------- 表情 Picker（Flyout，由 WinUI 自动处理边缘翻转/屏幕约束）----------

    private void PickerFlyout_Opening(object sender, object e)
    {
        // Flyout 打开时按需加载缩略图（避免后台常驻解码）。
        LoadPickerMemes();
    }

    private void LoadPickerMemes()
    {
        // 全部表情视图：GetMemes(null) 返回所有分类（复用引擎既有语义，不过滤）。
        var models = _engine.GetMemes(IsAllMemesView ? null : _currentCategory).ToList();
        _pickerMemes = models.Select(m => new MemeViewModel(m)).ToList();
        PickerRepeater.ItemsSource = _pickerMemes;

        PickerEmptyHint.Visibility = _pickerMemes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // 薄适配器：Picker 项 VM 经 Tag 承载（不在 DataTemplate 内、无法直绑 ICommand），
    // 这里把 Tapped 事件转发给 VM 的 SendMemeCommand（实际发送逻辑经 SendToExternalRequested 事件回 Page）。
    private void PickerItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not MemeViewModel vm)
            return;
        if (!File.Exists(vm.LocalPath)) return;

        // 关闭 Picker 先把焦点交还给外部应用：点击瞬间前台通常仍是用户正在用的应用(QQ 等)，
        // ResolveExternalPasteTarget 会优先返回 _fgTimer 记录的外部窗口（Mini 模式下回退到
        // 本窗口获得焦点前的前台=外部应用，避免把图粘贴回自己身上）。
        PickerFlyout.Hide();
        ViewModel.SendMemeCommand.Execute(vm);
    }

    // 从 Picker 拖出图片到外部（QQ/输入框等）：复用 MainPage 的稳定单图拖出逻辑。
    // 仅声明 Copy（不移动文件），并提供 Bitmap（老客户端）与 StorageItems（文件拖出，动态图）。
    private void PickerItem_DragStarting(object sender, Microsoft.UI.Xaml.DragStartingEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not MemeViewModel vm)
            return;
        if (string.IsNullOrEmpty(vm.LocalPath) || !File.Exists(vm.LocalPath))
            return;

        // 复用 ImageDragHelper：装 StorageItems + 单张非 GIF 的 Bitmap 兜底，GIF 仅文件拖出。
        ImageDragHelper.ConfigureDragOut(e.Data, new[] { vm.LocalPath }, ConfigService.Config.StorageFileDrag);
        Logger.Log($"[Mini] 拖出 1 张图片 ({Path.GetFileName(vm.LocalPath)})");
    }

    // ---------- 顶部按钮 ----------

    // IImageReleasablePage：窗口隐藏/切模式前由 MainWindow 统一调用。
    // Picker 每次 Opening 都会 new 一批新 VM 并重新赋 ItemsSource，旧批次若不断引用会累积；
    // 这里遍历旧 VM 调 ClearImages() 断开其 BitmapImage，并把 Repeater 的 ItemsSource 置空，
    // 让 Image 容器从可视化树移除、框架释放 GPU 纹理。仅断引用，GC 由 MainWindow 统一执行。
    // detachItemsSource 参数忽略：Picker 容器每次都需摘（每次 Opening 都会换新批次）。
    public void ReleaseImages(bool detachItemsSource)
    {
        // 顺序有讲究：趁 Flyout 还开着（元素确实在可视化树上、可 enumerate）先断 Image.Source。
        // ClearImages() 只清 VM 字段且刻意不触发 PropertyChanged，已 realize 元素上的 Image.Source
        // 仍引用旧 BitmapImage —— 不显式置 null 纹理不会释放，而 ItemsSource=null 触发的元素回收
        // 要等一次布局，Flyout 卷起的 Popup 子树根本不参与布局（同 MainPage 预览浮窗
        // "PreviewImage.Source = null" 那条路径）。所以顺序必须是：断元素源 → 收 Popup → 摘 ItemsSource。
        DetachRepeaterItemImages();

        foreach (var vm in _pickerMemes)
            vm.ClearImages();
        // 断列表对 VM 的引用：全部表情视图下 Picker 会一次建出全库的 VM，
        // 隐藏期间无需常驻（下次 Opening 按当前分类重新加载）。
        _pickerMemes.Clear();

        // 收起 Popup：SW_HIDE 主窗口不会自动关 Flyout，Popup 子树（含未被回收的元素）会一直挂在
        // 窗口上，也避免再次呼出窗口时 Picker 残留打开。未打开时 Hide() 是 no-op。
        PickerFlyout.Hide();

        PickerRepeater.ItemsSource = null;
    }

    // 把 PickerRepeater 已 realize 元素里 Image 的 Source 置 null，断开元素对 BitmapImage 的引用
    // （纹理随 BitmapImage 失去引用被框架回收）。未 realize 的索引 TryGetElement 返回 null，天然跳过。
    private void DetachRepeaterItemImages()
    {
        int count = PickerRepeater.ItemsSourceView?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            if (PickerRepeater.TryGetElement(i) is UIElement element)
                ClearImageSources(element);
        }
    }

    // 递归清空子树里的 Image.Source：不依赖模板固定层级（当前模板根是 Grid、其子级为 Image，
    // 模板将来加包裹层也能找到）。
    private static void ClearImageSources(DependencyObject root)
    {
        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < children; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Image image)
                image.Source = null;
            else
                ClearImageSources(child);
        }
    }

    // ---------- 拖入导入（XAML DataPackage + Win32 WM_DROPFILES 转发）----------

    // 实现 IExternalDropPage：由 MainWindow 在 WM_DROPFILES 时转发路径。
    public void HandleExternalDropPaths(List<string> paths)
    {
        _ = TryImportAsync(paths);
    }

    // 统一拖入入口：调用 ImageDragHelper 导入到当前分类。
    //  - 被“忙”守卫拒绝（已有导入进行中）→ 显示“导入中”并临时禁止拖入。
    //  - 成功 → 显示“成功导入 x 张到 xxx 分类”，7s 后自动恢复。
    private async Task TryImportAsync(List<string> paths)
    {
        // 导入目标复用 Full 规则：全部表情视图落到“未分类”，否则当前分类。
        var (success, imported) = await ImageDragHelper.ImportPathsAsync(paths, ImportTargetCategory);
        if (!success)
            ShowImportBusy();
        else
            ShowImportSuccess(imported);
    }

    // 导入成功：显示“成功导入 x 张到 xxx 分类”，7s 后自动恢复默认提示。
    private void ShowImportSuccess(int imported)
    {
        // 取消上一条恢复定时，避免互相抢占
        _hintRestoreCts?.Cancel();
        _hintRestoreCts = new System.Threading.CancellationTokenSource();

        // 全部表情视图下导入实际落到“未分类”，展示其本地化名而非 AllMemes 字面。
        string targetName = IsAllMemesView
            ? Localization.Get("Category_Uncategorized")
            : _currentCategory;
        string text = imported > 0
            ? string.Format(Localization.Get("Mini_ImportSuccess"), imported, targetName)
            : Localization.Get("Mini_ImportDuplicate");

        MiniDropHintText.Text = text;
        DropHint.Visibility = Visibility.Collapsed;

        // 导入成功后若 Picker 浮窗已开，刷新其 GridView（浮窗展示的必定是当前分类）。
        if (PickerFlyout.IsOpen)
            LoadPickerMemes();

        var token = _hintRestoreCts.Token;
        _ = Task.Delay(ImportHintRestoreDelay, token).ContinueWith(_ =>
        {
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() => MiniDropHintText.Text = Localization.Get("Mini_DropHint"));
        }, TaskScheduler.Default);
    }

    // 导入进行中（被忙守卫拒绝）：禁用整页拖入（显示禁止光标）并临时把提示文字改为“导入中”，
    // 待 DataEngine 不再忙后自动恢复拖入与默认提示。
    private void ShowImportBusy()
    {
        _hintRestoreCts?.Cancel();

        RootGrid.AllowDrop = false;
        DropHint.Visibility = Visibility.Collapsed;
        MiniDropHintText.Text = Localization.Get("Mini_ImportBusy");
        Logger.Log("[Mini] 拖入被拒：导入进行中");

        // 轮询直到不再忙，恢复拖入与提示文字
        DispatcherQueue.TryEnqueue(async () =>
        {
            while (_engine.IsBusyWriting)
                await Task.Delay(150);
            RootGrid.AllowDrop = true;
            MiniDropHintText.Text = Localization.Get("Mini_DropHint");
        });
    }

    // ---------- XAML 层拖入（QQ 等来源的 DataPackage 拖拽）----------

    private async void Grid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) ||
            e.DataView.Contains(StandardDataFormats.Bitmap))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            DropHint.Visibility = Visibility.Visible;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private void Grid_DragLeave(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
    }

    private async void Grid_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        var paths = await ImageDragHelper.CollectDropPathsAsync(e.DataView);
        if (paths.Count > 0)
            await TryImportAsync(paths);
    }
}
