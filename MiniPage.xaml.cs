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

public sealed partial class MiniPage : Page, IExternalDropPage
{
    // Picker 打开时才按需加载的缩略图列表（避免后台常驻解码）。
    private List<MemeViewModel> _pickerMemes = new();

    // Mini 模式当前选中的分类（拖入/点选都基于它）。
    private string _currentCategory = MemeDataEngine.UncategorizedCategory;

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

    private void MiniPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // 离开 Mini 时取消标题栏注册（MainPage 加载时会重新注册自己的标题栏）。
        App.MainWindow.SetTitleBarElement(null);
    }

    // ---------- 分类下拉 ----------

    private void LoadCategories()
    {
        var cats = App.DataEngine.GetCategories();
        if (cats.Count == 0)
            App.DataEngine.EnsureDefaultCategory();
        cats = App.DataEngine.GetCategories();

        CategoryCombo.ItemsSource = cats;

        var last = App.DataEngine.Config.LastCategory;
        var target = cats.FirstOrDefault(c => c.Equals(last, StringComparison.OrdinalIgnoreCase))
                     ?? cats.FirstOrDefault();
        if (target != null)
        {
            CategoryCombo.SelectedItem = target;
            _currentCategory = target;
        }
        else
        {
            _currentCategory = MemeDataEngine.UncategorizedCategory;
        }
    }

    private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryCombo.SelectedItem is string cat && !string.IsNullOrEmpty(cat))
        {
            _currentCategory = cat;
            _ = App.DataEngine.UpdateConfigAsync(c => c.LastCategory = cat);
            if (PickerPopup.IsOpen)
                LoadPickerMemes();
        }
    }

    // ---------- 表情 Picker（Popup，可超出窗口边界）----------

    private void PickerButton_Click(object sender, RoutedEventArgs e)
    {
        if (PickerPopup.IsOpen)
        {
            PickerPopup.IsOpen = false;
            return;
        }

        // 定位到 Picker 按钮正上方（Popup 设了 ShouldConstrainToRootBounds=False，
        // 可超出窗口上沿显示）。坐标相对 Page（即窗口内容区左上角）。
        var btnPos = PickerButton.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
        PickerPopup.HorizontalOffset = btnPos.X;
        PickerPopup.VerticalOffset = btnPos.Y - 312 - 4; // 面板高 ~312 + 间距

        LoadPickerMemes();
        PickerPopup.IsOpen = true;
    }

    private void LoadPickerMemes()
    {
        var models = App.DataEngine.GetMemes(_currentCategory).ToList();
        _pickerMemes = models.Select(m => new MemeViewModel(m)).ToList();
        PickerRepeater.ItemsSource = _pickerMemes;

        PickerEmptyHint.Visibility = _pickerMemes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PickerItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MemeViewModel vm)
            return;
        if (!File.Exists(vm.LocalPath)) return;

        // 关闭 Picker 先把焦点交还给外部应用：点击瞬间前台通常仍是用户正在用的应用(QQ 等)，
        // ResolveExternalPasteTarget 会优先返回 _fgTimer 记录的外部窗口（Mini 模式下回退到
        // 本窗口获得焦点前的前台=外部应用，避免把图粘贴回自己身上）。
        PickerPopup.IsOpen = false;

        var target = App.MainWindow.ResolveExternalPasteTarget();
        await PasteService.OutputMemeToCursorAsync(vm.LocalPath, target);
    }

    // 从 Picker 拖出图片到外部（QQ/输入框等）：复用 MainPage 的稳定单图拖出逻辑。
    // 仅声明 Copy（不移动文件），并提供 Bitmap（老客户端）与 StorageItems（文件拖出，动态图）。
    private void PickerItem_DragStarting(object sender, Microsoft.UI.Xaml.DragStartingEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MemeViewModel vm)
            return;
        if (string.IsNullOrEmpty(vm.LocalPath) || !File.Exists(vm.LocalPath))
            return;

        // 复用 ImageDragHelper：装 StorageItems + 单张非 GIF 的 Bitmap 兜底，GIF 仅文件拖出。
        ImageDragHelper.ConfigureDragOut(e.Data, new[] { vm.LocalPath }, App.DataEngine.Config.StorageFileDrag);
    }

    // ---------- 顶部按钮 ----------

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
        var (success, imported) = await ImageDragHelper.ImportPathsAsync(paths, _currentCategory);
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

        string text = imported > 0
            ? string.Format(Localization.Get("Mini_ImportSuccess"), imported, _currentCategory)
            : Localization.Get("Mini_ImportDuplicate");

        MiniDropHintText.Text = text;
        DropHint.Visibility = Visibility.Collapsed;

        // 导入成功后若 Picker 浮窗已开，刷新其 GridView（浮窗展示的必定是当前分类）。
        if (PickerPopup.IsOpen)
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
