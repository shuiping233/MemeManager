using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MemeManager.Data;
using MemeManager.Models;
using Windows.Storage.Pickers;
using System.Diagnostics;

namespace MemeManager;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();

        var cfg = App.DataEngine.Config;
        ThemeComboBox.SelectedIndex = (int)cfg.Theme;
        StoragePathBox.Text = cfg.StoragePath;
        HotKeyBox.Text = MainWindow.HotKeyText(cfg.HotKeyModifiers, cfg.HotKeyVk);
        SaveLogToggle.IsOn = cfg.SaveLogFile;
        EcoModeToggle.IsOn = cfg.EcoMode;
        AutoStartToggle.IsOn = StartupManager.IsEnabled();
        UseControlReuseToggle.IsOn = cfg.UseControlReuse;
        DragOutputAsImageToggle.IsOn = cfg.DragOutputAsImage;

        // 预览图设置：缺失时用默认 800x600 / 400ms
        PreviewMaxWidthBox.Text = (cfg.PreviewMaxWidth > 0 ? cfg.PreviewMaxWidth : 800).ToString();
        PreviewMaxHeightBox.Text = (cfg.PreviewMaxHeight > 0 ? cfg.PreviewMaxHeight : 600).ToString();
        PreviewDelayBox.Text = (cfg.PreviewDelayMs > 0 ? cfg.PreviewDelayMs : 400).ToString();

        // 进入设置时记录已有的有效路径，作为手动输入校验失败时的回退基准
        _originalStoragePath = cfg.StoragePath;

        this.KeyDown += SettingsPage_KeyDown;
    }

    // 进入设置时已有的有效路径（校验失败回退用，而非默认路径）
    private string? _originalStoragePath;

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 即选即预览：立刻切换主题，无需点“完成”
        var theme = (ThemeMode)ThemeComboBox.SelectedIndex;
        App.DataEngine.Config.Theme = theme;
        App.ApplyTheme();
    }

    private void SettingsPage_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_recording)
        {
            e.Handled = true;

            // 只记录修饰键阶段，等用户按下真正的键
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;
            bool ctrl = IsDown(Windows.System.VirtualKey.Control) || IsDown(Windows.System.VirtualKey.LeftControl) || IsDown(Windows.System.VirtualKey.RightControl);
            bool shift = IsDown(Windows.System.VirtualKey.Shift) || IsDown(Windows.System.VirtualKey.LeftShift) || IsDown(Windows.System.VirtualKey.RightShift);
            bool alt = IsDown(Windows.System.VirtualKey.Menu) || IsDown(Windows.System.VirtualKey.LeftMenu) || IsDown(Windows.System.VirtualKey.RightMenu);
            bool win = IsDown(Windows.System.VirtualKey.LeftWindows) || IsDown(Windows.System.VirtualKey.RightWindows);

            // 组合键的“主键”不能是纯修饰键
            bool isModifier = e.Key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Shift
                or Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftControl
                or Windows.System.VirtualKey.RightControl or Windows.System.VirtualKey.LeftShift
                or Windows.System.VirtualKey.RightShift or Windows.System.VirtualKey.LeftMenu
                or Windows.System.VirtualKey.RightMenu or Windows.System.VirtualKey.LeftWindows
                or Windows.System.VirtualKey.RightWindows;

            if (isModifier) return;

            uint mods = 0;
            if (ctrl) mods |= 0x2;
            if (shift) mods |= 0x4;
            if (alt) mods |= 0x1;
            if (win) mods |= 0x8;

            ushort vk = (ushort)e.Key;
            StopRecording();
            App.MainWindow.ApplyHotKeyConfig(mods, vk);
            HotKeyBox.Text = MainWindow.HotKeyText(mods, vk);
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            // 焦点已在“完成”按钮上时由按钮自身处理，避免重复触发保存
            if (ReferenceEquals(e.OriginalSource, CloseButton)) return;
            CloseButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private static bool IsDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 进入设置页时把焦点放到“完成”按钮上，避免焦点落在首个可聚焦控件
        // （主题 ComboBox）导致回车变成打开下拉菜单而非确认。
        CloseButton.Focus(FocusState.Programmatic);
    }

    private void SaveLogToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 改动延后到点击“完成”时保存
    }

    private void EcoModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 即时生效：切换进程级效率模式（保存延后到点击“完成”）
        EcoQos.ApplyProcessLevel(EcoModeToggle.IsOn);
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 改动延后到点击“完成”时保存
    }

    private void UseControlReuseToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 改动延后到点击“完成”时保存
    }

    private void DragOutputAsImageToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 改动延后到点击“完成”时保存
    }

    // 仅允许输入非负整数（数字 + 空串），非数字内容直接拒绝
    private void PreviewNumberBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        args.Cancel = !(string.IsNullOrEmpty(args.NewText) || args.NewText.All(char.IsDigit));
    }

    private void PreviewResolution_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 仅做合法性校验，真正保存延后到点击“完成”
        if (!double.TryParse(PreviewMaxWidthBox.Text, out double w) || w <= 0) return;
        if (!double.TryParse(PreviewMaxHeightBox.Text, out double h) || h <= 0) return;
    }

    private void PreviewDelay_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 仅做合法性校验，真正保存延后到点击“完成”
        if (!int.TryParse(PreviewDelayBox.Text, out int ms) || ms <= 0) return;
    }

    private bool _recording;

    private void RecordHotKeyButton_Click(object sender, RoutedEventArgs e)
    {
        _recording = true;
        HotKeyBox.Text = "请按下组合键…";
        RecordHotKeyButton.Content = "取消";
        RecordHotKeyButton.Click -= RecordHotKeyButton_Click;
        RecordHotKeyButton.Click += CancelRecord_Click;
        this.Focus(FocusState.Programmatic);
    }

    private void CancelRecord_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        var cfg = App.DataEngine.Config;
        HotKeyBox.Text = MainWindow.HotKeyText(cfg.HotKeyModifiers, cfg.HotKeyVk);
    }

    private void StopRecording()
    {
        _recording = false;
        RecordHotKeyButton.Content = "录制…";
        RecordHotKeyButton.Click -= CancelRecord_Click;
        RecordHotKeyButton.Click += RecordHotKeyButton_Click;
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await PickerHelper.PickFolderAsync();
            if (folder != null)
            {
                Logger.Log($"[Settings] BrowseButton_Click: 成功选择文件夹: {folder}");
                StoragePathBox.Text = folder;

                // 立即写入并刷新：Flyout 打开文件选择器会失焦，可能导致设置页实例被换，
                // 若延后到 SaveAsync 会读到旧实例的默认值
                await App.DataEngine.UpdateConfigAsync(cfg => cfg.StoragePath = folder);
                App.MainWindow.ReloadData();
                Logger.Log($"[Settings] BrowseButton_Click: 已立即保存存放路径并刷新: {folder}");
            }
            else
            {
                Logger.Log("[Settings] BrowseButton_Click: 用户取消或未选择任何文件夹");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] BrowseButton_Click 异常: {ex}");
            await ShowErrorAsync("打开文件夹选择器失败", ex.ToString());
        }
    }

    private async Task ShowErrorAsync(string title, string detail)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = detail,
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] 弹出错误对话框也失败: {ex}");
        }
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = StoragePathBox.Text;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            // 确保目录存在
            System.IO.Directory.CreateDirectory(path);
            await Windows.System.Launcher.LaunchFolderPathAsync(path);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] 打开文件夹失败: {ex.Message}");
        }
    }

    // 用户手动修改路径文本框时校验：目录存在则记录，不存在则提示并回退到进入设置前的有效路径
    private bool _revertingPath;
    private async void StoragePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_revertingPath) return;

        var text = StoragePathBox.Text?.Trim() ?? string.Empty;
        // 空字符串暂不打扰（用户可能正在输入中）
        if (string.IsNullOrWhiteSpace(text)) return;

        if (System.IO.Directory.Exists(text))
        {
            // 有效路径：仅记录，真正保存延后到点击“完成”
            return;
        }

        // 目录不存在：弹窗提示并回退到进入设置前保存的有效路径
        try
        {
            var dialog = new ContentDialog
            {
                Title = "路径不存在",
                Content = $"指定的存放路径不存在：\n{text}\n\n已恢复为之前的有效路径。",
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] 路径校验弹窗失败: {ex.Message}");
        }

        var fallback = _originalStoragePath ?? MemeDataEngine.DefaultStoragePath();
        _revertingPath = true;
        StoragePathBox.Text = fallback;
        _revertingPath = false;
    }

    public async Task SaveAsync()
    {
        // 防止重复保存：点击“完成”已保存一次，浮窗 Closed 事件又会触发一次
        if (_saved) return;
        _saved = true;

        var theme = (ThemeMode)ThemeComboBox.SelectedIndex;

        double.TryParse(PreviewMaxWidthBox.Text, out double pw);
        double.TryParse(PreviewMaxHeightBox.Text, out double ph);
        int.TryParse(PreviewDelayBox.Text, out int delay);

        string? newStoragePath = null;
        bool pathChanged = false;
        var typedPath = StoragePathBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(typedPath) && System.IO.Directory.Exists(typedPath))
        {
            newStoragePath = typedPath;
            pathChanged = true;
        }

        var prev = App.DataEngine.Config;
        double prevW = prev.PreviewMaxWidth, prevH = prev.PreviewMaxHeight;
        int prevDelay = prev.PreviewDelayMs;

        await App.DataEngine.UpdateConfigAsync(cfg =>
        {
            cfg.Theme = theme;
            cfg.SaveLogFile = SaveLogToggle.IsOn;
            cfg.EcoMode = EcoModeToggle.IsOn;
            cfg.AutoStart = AutoStartToggle.IsOn;
            cfg.UseControlReuse = UseControlReuseToggle.IsOn;
            cfg.DragOutputAsImage = DragOutputAsImageToggle.IsOn;
            if (pw > 0) cfg.PreviewMaxWidth = pw;
            if (ph > 0) cfg.PreviewMaxHeight = ph;
            if (delay > 0) cfg.PreviewDelayMs = delay;
            if (pathChanged) cfg.StoragePath = newStoragePath!;
        });

        if (pw > 0 && ph > 0 && (pw != prevW || ph != prevH))
            Logger.Log($"[Settings] 预览图最大分辨率: {prevW}x{prevH} -> {pw}x{ph}");
        if (delay > 0 && delay != prevDelay)
            Logger.Log($"[Settings] 预览图触发延时: {prevDelay}ms -> {delay}ms");

        App.ApplyTheme();

        // 预览分辨率 / 存放路径变化：重建主窗口数据以生效
        if ((pw > 0 && ph > 0) || pathChanged)
            App.MainWindow.ReloadData();

        if (delay > 0)
            App.MainWindow.ApplyPreviewDelayFromConfig();

        // 复用策略切换：应用新策略并刷新当前列表使其立即生效
        App.MainWindow.ApplyListStrategyFromConfig();
        App.MainWindow.ReloadData();

        bool ok = AutoStartToggle.IsOn ? StartupManager.Enable() : StartupManager.Disable();
        if (!ok)
            Logger.Log("[Settings] 设置开机自启失败（注册表写入被拒绝）");

        Logger.Log("[Settings] 配置已保存");
    }

    // 请求关闭（由宿主 Flyout 监听后 Hide）
    public event EventHandler? RequestClose;

    // 是否已保存过（避免“完成”点击与浮窗 Closed 事件重复保存）
    private bool _saved;
    public bool IsSaved => _saved;

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveAsync();
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
