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
        AutoStartToggle.IsOn = StartupManager.IsEnabled();

        // 进入设置时记录“之前保存的有效路径”，作为手动输入校验失败时的回退基准
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
            CloseButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private static bool IsDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 进入设置页即聚焦，确保能接收 Enter 键
        this.Focus(FocusState.Programmatic);
    }

    private void SaveLogToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 即时生效：切换日志文件保存
        App.DataEngine.Config.SaveLogFile = SaveLogToggle.IsOn;
        Logger.Log($"[Settings] 保存日志文件已{(SaveLogToggle.IsOn ? "启用" : "关闭")}");
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 即时生效：写/删 HKCU\...\Run 注册表键值
        bool ok = AutoStartToggle.IsOn ? StartupManager.Enable() : StartupManager.Disable();
        if (!ok)
        {
            // 写注册表失败（如权限不足），回滚开关状态
            AutoStartToggle.IsOn = StartupManager.IsEnabled();
            Logger.Log("[Settings] 设置开机自启失败（注册表写入被拒绝）");
        }
        else
        {
            Logger.Log($"[Settings] 开机自启已{(AutoStartToggle.IsOn ? "启用" : "关闭")}");
        }
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

                // 立即把新路径写入引擎并持久化，不依赖关闭时的 SaveAsync 回读
                // （Flyout 打开文件选择器会失焦，可能导致设置页实例被换，SaveAsync 读到的是旧实例的默认值）
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

    // 用户手动修改路径文本框时校验：
    //  - 目录存在 → 立即写入 config 并刷新主窗口
    //  - 目录不存在 → 弹窗提示并回退到进入设置前的有效路径
    private bool _revertingPath;
    private async void StoragePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_revertingPath) return;

        var text = StoragePathBox.Text?.Trim() ?? string.Empty;
        // 空字符串暂不打扰（用户可能正在输入中）
        if (string.IsNullOrWhiteSpace(text)) return;

        if (System.IO.Directory.Exists(text))
        {
            // 有效路径：立即持久化并刷新，与“浏览”行为一致
            await App.DataEngine.UpdateConfigAsync(cfg => cfg.StoragePath = text);
            App.MainWindow.ReloadData();
            Logger.Log($"[Settings] 手动输入有效路径已保存并刷新: {text}");
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
        var theme = (ThemeMode)ThemeComboBox.SelectedIndex;
        // 注意：存放路径(StoragePath)由 BrowseButton_Click 选中后立即持久化，
        // 此处不再回写，避免 Flyout 失焦导致实例被换、用默认值覆盖已保存的新路径。

        await App.DataEngine.UpdateConfigAsync(cfg =>
        {
            cfg.Theme = theme;
            cfg.SaveLogFile = SaveLogToggle.IsOn;
        });

        App.ApplyTheme();
    }

    // 请求关闭（由宿主 Flyout 监听后 Hide）
    public event EventHandler? RequestClose;

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveAsync();
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
