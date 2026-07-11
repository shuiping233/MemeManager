using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        EcoModeToggle.IsOn = cfg.EcoMode;
        SaveLogToggle.IsOn = cfg.SaveLogFile;

        this.KeyDown += SettingsPage_KeyDown;
    }

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

    private void EcoModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 即时生效：切换效率模式
        App.DataEngine.Config.EcoMode = EcoModeToggle.IsOn;
        if (EcoModeToggle.IsOn)
            App.ApplyEcoMode();
        else
            App.ResetEcoMode();
    }

    private void SaveLogToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 即时生效：切换日志文件保存
        App.DataEngine.Config.SaveLogFile = SaveLogToggle.IsOn;
        Logger.Log($"[Settings] 保存日志文件已{(SaveLogToggle.IsOn ? "启用" : "关闭")}");
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

    public async Task SaveAsync()
    {
        var theme = (ThemeMode)ThemeComboBox.SelectedIndex;
        // 注意：存放路径(StoragePath)由 BrowseButton_Click 选中后立即持久化，
        // 此处不再回写，避免 Flyout 失焦导致实例被换、用默认值覆盖已保存的新路径。
        var eco = EcoModeToggle.IsOn;

        await App.DataEngine.UpdateConfigAsync(cfg =>
        {
            cfg.Theme = theme;
            cfg.EcoMode = eco;
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
