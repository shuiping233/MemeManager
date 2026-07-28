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
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace MemeManager;

public sealed partial class MiniPage : Page, IExternalDropPage
{
    // Picker 打开时才按需加载的缩略图列表（避免后台常驻解码）。
    private List<MemeViewModel> _pickerMemes = new();

    // Mini 模式当前选中的分类（拖入/点选都基于它）。
    private string _currentCategory = MemeDataEngine.UncategorizedCategory;

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
        App.MainWindow.SetMiniTitleBar(DragBar);
    }

    private void MiniPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // 离开 Mini 时取消标题栏注册，避免影响 Full 模式。
        App.MainWindow.SetMiniTitleBar(null);
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
        // ResolveExternalPasteTarget 会优先返回 _fgTimer 记录的外部窗口。
        PickerPopup.IsOpen = false;

        var target = App.MainWindow.ResolveExternalPasteTarget();
        await PasteService.OutputMemeToCursorAsync(vm.LocalPath, target);
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
        _ = ImportDroppedFilesAsync(paths);
    }

    private async Task ImportDroppedFilesAsync(List<string> paths)
    {
        var images = paths
            .Where(p => File.Exists(p) && MainPage.IsImage(Path.GetExtension(p)))
            .ToList();
        if (images.Count == 0) return;

        var result = await App.DataEngine.ImportMemesAsync(images, _currentCategory);
        Logger.Log($"[Mini] 拖入导入：新增 {result.imported}，重复 {result.duplicate}（分类={_currentCategory}）");
    }

    // ---------- XAML 层拖入（QQ 等来源的 DataPackage 拖拽）----------

    private void Grid_DragOver(object sender, DragEventArgs e)
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
        var paths = new List<string>();

        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
                if (item is StorageFile file && MainPage.IsImage(file.FileType))
                    paths.Add(file.Path);
        }

        if (e.DataView.Contains(StandardDataFormats.Bitmap))
        {
            try
            {
                var streamRef = await e.DataView.GetBitmapAsync();
                using var stream = await streamRef.OpenReadAsync();
                var tempPath = Path.Combine(Path.GetTempPath(), $"meme_{Guid.NewGuid():N}.png");
                using (var outStream = File.Create(tempPath))
                {
                    await stream.AsStreamForRead().CopyToAsync(outStream);
                }
                paths.Add(tempPath);
            }
            catch (Exception ex) { Logger.Log("[Mini] 拖入(Bitmap)失败: " + ex.Message); }
        }

        if (paths.Count > 0)
            HandleExternalDropPaths(paths);
    }
}
