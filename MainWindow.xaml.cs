using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
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

    // 记录本窗口激活前的前台窗口（通常是正在聊天的目标应用），用于粘贴时回投 Ctrl+V
    private IntPtr _prevActiveHwnd;
    private bool _isActive;

    public MainWindow()
    {
        InitializeComponent();

        _hWnd = WindowNative.GetWindowHandle(this);

        int exStyle = NativeMethods.GetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOPMOST);

        NativeMethods.RegisterHotKey(_hWnd, HOTKEY_ID, NativeMethods.MOD_ALT, 0x45);

        _subclassProc = NewWindowProc;
        NativeMethods.SetWindowSubclass(_hWnd, _subclassProc, SUBCLASS_ID, IntPtr.Zero);

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(720, 640));

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
            _memeList.Add(new MemeViewModel(m));

        UpdateCategoryCounts();
    }

    private void UpdateCategoryCounts()
    {
        foreach (var c in _categoryList)
            c.Count = App.DataEngine.GetMemes(c.Name).Count;
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
            BatchBar.Visibility = Visibility.Visible;
        }
    }

    // ---------- 点击表情（非修改模式 = 粘贴） ----------

    private async void MemeItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 右键交给 RightTapped 处理
        if (e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
            return;

        if (sender is not FrameworkElement fe || fe.DataContext is not MemeViewModel clicked)
        {
            Log("MemeItem_PointerPressed: 取不到 MemeViewModel, sender=" + sender?.GetType().Name);
            return;
        }

        if (_editMode)
        {
            Log($"左键单击(多选模式): 切换选中 {clicked.Title}，新选中态={!clicked.IsSelected}");
            clicked.IsSelected = !clicked.IsSelected;
            return;
        }

        Log($"左键单击(发送模式): 发送图片 {clicked.Title} 到前台窗口 _prevActiveHwnd={_prevActiveHwnd}");
        IgnoreNextClipboardChange();
        await PasteService.OutputMemeToCursorAsync(clicked.LocalPath, _prevActiveHwnd);
        await App.DataEngine.IncrementUsageAsync(clicked.Hash);
    }

    private static MemeViewModel? FindMemeFromSource(object? source)
    {
        var element = source as DependencyObject;
        while (element != null)
        {
            if (element is FrameworkElement fe && fe.DataContext is MemeViewModel vm)
                return vm;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    // ---------- 拖拽：拖入导入 / 拖出到外部输入框 ----------

    private static void Log(string msg) => System.Diagnostics.Debug.WriteLine($"[MemeManager] {msg}");

    private void MemeGridView_DragOver(object sender, DragEventArgs e)
    {
        var hasItems = e.DataView.Contains(StandardDataFormats.StorageItems);
        var hasBitmap = e.DataView.Contains(StandardDataFormats.Bitmap);
        var hasText = e.DataView.Contains(StandardDataFormats.Text);
        Log($"DragOver from {sender?.GetType().Name}: hasStorageItems={hasItems}, hasBitmap={hasBitmap}, hasText={hasText}");
        if (hasItems || hasBitmap || hasText)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.IsCaptionVisible = false;
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void MemeGridView_Drop(object sender, DragEventArgs e)
    {
        Log("Drop 事件触发");
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            Log("Drop: 没有 StorageItems，忽略");
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        Log($"Drop: 拖入 {items.Count} 个项");
        bool any = false;
        foreach (var item in items)
        {
            Log($"Drop: 项 {item.Name} 是文件={item is StorageFile}, 类型={item.GetType().Name}");
            if (item is StorageFile file && IsImage(file.FileType))
            {
                Log($"Drop: 导入图片 {file.Path} 到分类 {_currentCategory}");
                var imported = await App.DataEngine.ImportMemeAsync(file.Path, _currentCategory);
                if (imported != null && imported.Category.Equals(_currentCategory, StringComparison.OrdinalIgnoreCase))
                {
                    _memeList.Add(new MemeViewModel(imported));
                    any = true;
                }
            }
        }
        if (any) UpdateCategoryCounts();
    }

    private void MemeItem_DragStarting(object sender, DragStartingEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MemeViewModel vm)
        {
            Log($"DragStarting: 拖出图片 {vm.Title} ({vm.FileName}) 路径={vm.LocalPath}");
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(vm.LocalPath).AsTask().Result;
            e.Data.SetStorageItems(new[] { file });
            e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }
        else
        {
            Log("DragStarting: 未找到 MemeViewModel，sender=" + sender?.GetType().Name);
        }
    }

    // ---------- 右键菜单 ----------

    private void MemeItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        Log("右键单击表情项: " + ((sender as FrameworkElement)?.DataContext as MemeViewModel)?.Title);
        if (sender is FrameworkElement fe && fe.DataContext is MemeViewModel vm)
        {
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
            flyout.ShowAt(fe, e.GetPosition(fe));
        }
    }

    // ---------- 批量操作 ----------

    private async void BatchImportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hWnd);
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".gif");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");

        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return;

        foreach (var file in files)
        {
            var imported = await App.DataEngine.ImportMemeAsync(file.Path, _currentCategory);
            if (imported != null)
                _memeList.Add(new MemeViewModel(imported));
        }
        UpdateCategoryCounts();
    }

    private async void BatchExportButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _memeList.Where(m => m.IsSelected).ToList();
        if (selected.Count == 0) return;

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, _hWnd);
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        await App.DataEngine.ExportMemesAsync(selected.Select(m => m.Model), folder.Path);
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

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.Content = new SettingsPage();
        SettingsFlyout.ShowAt(SettingsButton);
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

    private void ExitEditMode()
    {
        _editMode = false;
        EditButton.Content = "修改";
        BatchBar.Visibility = Visibility.Collapsed;
        foreach (var m in _memeList) m.IsSelected = false;
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
            System.Diagnostics.Debug.WriteLine($"[Paste] 导入失败: {ex.Message}");
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
            // 记录被我们抢走焦点的那个窗口，粘贴时把 Ctrl+V 投回给它
            int state = (int)wParam & 0xFFFF;
            _isActive = state != NativeMethods.WA_INACTIVE;
            if (state != NativeMethods.WA_INACTIVE && lParam != IntPtr.Zero)
            {
                _prevActiveHwnd = lParam;
            }
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        if (uMsg == NativeMethods.WM_HOTKEY && (int)wParam == HOTKEY_ID)
        {
            ToggleWindowVisibility();
            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ToggleWindowVisibility()
    {
        if (_isVisible)
        {
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_HIDE);
            _isVisible = false;
        }
        else
        {
            // 用 SHOWNOACTIVATE 显示：窗口置顶但不抢焦点，
            // 这样用户仍能直接点表情粘贴到原来的应用里
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_SHOWNOACTIVATE);
            RefreshMemes();
            _isVisible = true;
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_ID);
        if (_clipboardHooked)
            Clipboard.ContentChanged -= Clipboard_ContentChanged;
    }
}
