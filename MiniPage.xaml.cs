using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MemeManager.Models;
using MemeManager.ViewModels;
using MemeManager.Data;
using MemeManager.Helpers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace MemeManager;

public sealed partial class MiniPage : Page, IExternalDropPage, IImageReleasablePage
{
    // Picker 打开时才按需加载的缩略图列表（避免后台常驻解码）。
    private List<MemeViewModel> _pickerMemes = new();

    // “全部表情”视图下 _currentCategory 的取值（与 Full 一致：仅用于显示/日志，不参与判断）。
    private const string AllMemesCategory = "AllMemes";

    // Mini 模式当前选中的分类；空串 / AllMemesCategory 表示“全部表情”视图。
    private string _currentCategory = AllMemesCategory;

    // 是否处于“全部表情”视图（导入落到未分类，与 Full 一致）。
    private bool IsAllMemesView => string.IsNullOrEmpty(_currentCategory)
                                   || _currentCategory == AllMemesCategory;

    // 拖入/导入时的目标分类：全部表情视图落入“未分类”，否则按当前分类（复用 Full 规则）。
    private string ImportTargetCategory =>
        IsAllMemesView ? MemeDataEngine.UncategorizedCategory : _currentCategory;

    // 用于取消上一次“提示文字自动恢复”的定时任务，避免多条消息互相抢占。
    private System.Threading.CancellationTokenSource? _hintRestoreCts;

    // 导入成功提示自动恢复为默认文案的延迟时长（毫秒）。
    private const int ImportHintRestoreDelay = 7 * 1000;

    public MiniPage()
    {
        InitializeComponent();
        Loaded += MiniPage_Loaded;
        Unloaded += MiniPage_Unloaded;
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
        MiniDropHintText.Focus(FocusState.Programmatic);
    }

    private void MiniPage_Unloaded(object sender, RoutedEventArgs e)
    {
    }

    // ---------- 分类下拉 ----------

    private void LoadCategories()
    {
        var cats = App.DataEngine.GetCategories();
        if (cats.Count == 0)
            App.DataEngine.EnsureDefaultCategory();
        cats = App.DataEngine.GetCategories();

        // 复用 Full 的 CategoryViewModel：头部插入“全部表情”虚拟项（Name 空串），
        // 与 Full 的 _allMemesVm 约定一致（空名 = 全部表情）。
        var items = new System.Collections.Generic.List<ViewModels.CategoryViewModel>
        {
            new ViewModels.CategoryViewModel("", 0)
        };
        foreach (var c in cats)
            items.Add(new ViewModels.CategoryViewModel(c, App.DataEngine.GetMemes(c).Count));
        CategoryCombo.ItemsSource = items;

        var last = App.DataEngine.Config.LastCategory;
        if (string.IsNullOrEmpty(last))
        {
            // 上次停留在“全部表情”：选中头部虚拟项。
            CategoryCombo.SelectedIndex = 0;
            _currentCategory = AllMemesCategory;
        }
        else
        {
            var idx = items.FindIndex(v => !string.IsNullOrEmpty(v.Name)
                && v.Name.Equals(last, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                CategoryCombo.SelectedIndex = idx;
                _currentCategory = items[idx].Name;
            }
            else
            {
                CategoryCombo.SelectedIndex = 0;
                _currentCategory = AllMemesCategory;
            }
        }
    }

    private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryCombo.SelectedItem is ViewModels.CategoryViewModel vm)
        {
            // 空名 = “全部表情”视图（与 Full 约定一致）。
            if (string.IsNullOrEmpty(vm.Name))
            {
                _currentCategory = AllMemesCategory;
                _ = App.DataEngine.UpdateConfigAsync(c => c.LastCategory = "");
            }
            else
            {
                _currentCategory = vm.Name;
                _ = App.DataEngine.UpdateConfigAsync(c => c.LastCategory = vm.Name);
            }
            ReleaseImages();
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
        var models = App.DataEngine.GetMemes(IsAllMemesView ? null : _currentCategory).ToList();
        _pickerMemes = models.Select(m => new MemeViewModel(m)).ToList();
        PickerRepeater.ItemsSource = _pickerMemes;

        PickerEmptyHint.Visibility = _pickerMemes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PickerItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not MemeViewModel vm)
            return;
        if (!File.Exists(vm.LocalPath)) return;

        // 关闭 Picker 先把焦点交还给外部应用：点击瞬间前台通常仍是用户正在用的应用(QQ 等)，
        // ResolveExternalPasteTarget 会优先返回 _fgTimer 记录的外部窗口（Mini 模式下回退到
        // 本窗口获得焦点前的前台=外部应用，避免把图粘贴回自己身上）。
        PickerFlyout.Hide();

        var target = App.MainWindow.ResolveExternalPasteTarget();
        Logger.Log($"[Mini] 点击发送图片 ({Path.GetFileName(vm.LocalPath)}) -> 目标={target:X}");
        await PasteService.OutputMemeToCursorAsync(vm.LocalPath, target);
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
        ImageDragHelper.ConfigureDragOut(e.Data, new[] { vm.LocalPath }, App.DataEngine.Config.StorageFileDrag);
        Logger.Log($"[Mini] 拖出 1 张图片 ({Path.GetFileName(vm.LocalPath)})");
    }

    // ---------- 顶部按钮 ----------

    // IImageReleasablePage：窗口隐藏/切模式前由 MainWindow 统一调用。
    // Picker 每次 Opening 都会 new 一批新 VM 并重新赋 ItemsSource，旧批次若不断引用会累积；
    // 这里遍历旧 VM 调 ClearImages() 断开其 BitmapImage，并把 Repeater 的 ItemsSource 置空，
    // 让 Image 容器从可视化树移除、框架释放 GPU 纹理。仅断引用，GC 由 MainWindow 统一执行。
    public void ReleaseImages()
    {
        if (_pickerMemes != null)
        {
            foreach (var vm in _pickerMemes)
                vm.ClearImages();
        }
        PickerRepeater.ItemsSource = null;
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        // 替代系统最大化：切回完整模式
        App.MainWindow.SwitchMode(AppMode.Full);
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
            while (App.DataEngine.IsBusyWriting)
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
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            DropHint.Visibility = Visibility.Visible;
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
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
