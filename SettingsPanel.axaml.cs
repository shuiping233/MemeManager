using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MemeManager.Data;
using MemeManager.Models;

namespace MemeManager;

public sealed partial class SettingsPanel : UserControl
{
    private string? _originalStoragePath;
    private bool _recording;
    // 防止 SaveAsync 并发/重复执行；每次打开面板时由 PrepareShow 重置。
    private bool _saveStarted;

    public event EventHandler? Finished;

    public SettingsPanel()
    {
        InitializeComponent();

        PrepareShow();
        this.KeyDown += SettingsPage_KeyDown;
    }

    /// <summary>
    /// 每次 Flyout 打开时调用：从最新配置重新填充控件，并重置保存标志。
    /// 因为 Flyout 会复用同一个 SettingsPanel 实例，必须重置，否则二次打开无法再次保存。
    /// </summary>
    public void PrepareShow()
    {
        var cfg = App.DataEngine.Config;
        ThemeComboBox.SelectedIndex = (int)cfg.Theme;
        StoragePathBox.Text = cfg.StoragePath;
        HotKeyBox.Text = MainWindow.HotKeyText(cfg.HotKeyModifiers, cfg.HotKeyVk);
        SaveLogToggle.IsChecked = cfg.SaveLogFile;
        EcoModeToggle.IsChecked = cfg.EcoMode;
        AutoStartToggle.IsChecked = StartupManager.IsEnabled();
        UseControlReuseToggle.IsChecked = cfg.UseControlReuse;
        DragOutputAsImageToggle.IsChecked = cfg.DragOutputAsImage;
        ExplorerStyleMultiSelectToggle.IsChecked = cfg.ExplorerStyleMultiSelect;

        PreviewMaxWidthBox.Text = (cfg.PreviewMaxWidth > 0 ? cfg.PreviewMaxWidth : 640).ToString();
        PreviewMaxHeightBox.Text = (cfg.PreviewMaxHeight > 0 ? cfg.PreviewMaxHeight : 480).ToString();
        PreviewDelayBox.Text = (cfg.PreviewDelayMs > 0 ? cfg.PreviewDelayMs : 500).ToString();

        _originalStoragePath = cfg.StoragePath;
        _saveStarted = false;
    }

    private async void ThemeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var theme = (ThemeMode)ThemeComboBox.SelectedIndex;
        App.DataEngine.Config.Theme = theme;
        App.ApplyTheme();
    }

    private void SettingsPage_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_recording)
        {
            e.Handled = true;
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            bool win = e.KeyModifiers.HasFlag(KeyModifiers.Meta);

            bool isModifier = e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
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
    }

    private void SaveLogToggle_Toggled(object? sender, RoutedEventArgs e) { }
    private void EcoModeToggle_Toggled(object? sender, RoutedEventArgs e)
        => EcoQos.ApplyProcessLevel(EcoModeToggle.IsChecked == true);

    private void AutoStartToggle_Toggled(object? sender, RoutedEventArgs e) { }
    private void UseControlReuseToggle_Toggled(object? sender, RoutedEventArgs e) { }
    private void DragOutputAsImageToggle_Toggled(object? sender, RoutedEventArgs e) { }
    private void ExplorerStyleMultiSelectToggle_Toggled(object? sender, RoutedEventArgs e) { }

    private void PreviewResolution_TextChanged(object? sender, TextChangedEventArgs e) { }
    private void PreviewDelay_TextChanged(object? sender, TextChangedEventArgs e) { }

    private async void RecordHotKeyButton_Click(object? sender, RoutedEventArgs e)
    {
        _recording = true;
        HotKeyBox.Text = "请按下组合键…";
        RecordHotKeyButton.Content = "取消";
        RecordHotKeyButton.Click -= RecordHotKeyButton_Click;
        RecordHotKeyButton.Click += CancelRecord_Click;
        this.Focus();
    }

    private void CancelRecord_Click(object? sender, RoutedEventArgs e)
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

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        App.MainWindow.IsFilePickerOpen = true;
        try
        {
            var folder = await PickerHelper.PickFolderAsync(App.MainWindow);
            if (folder != null)
            {
                Logger.Log($"[Settings] BrowseButton_Click: 成功选择文件夹: {folder}");
                StoragePathBox.Text = folder;
                await App.DataEngine.UpdateConfigAsync(cfg => cfg.StoragePath = folder);
                App.MainWindow.ReloadData();
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] BrowseButton_Click 异常: {ex}");
            await Dialogs.ShowMessageAsync(App.MainWindow, "打开文件夹选择器失败", ex.ToString());
        }
        finally { App.MainWindow.IsFilePickerOpen = false; }
    }

    private async void OpenFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var path = StoragePathBox.Text;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Log($"[Settings] 打开文件夹失败: {ex.Message}"); }
    }

    private bool _revertingPath;
    private async void StoragePathBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_revertingPath) return;
        var text = StoragePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        if (Directory.Exists(text)) return;

        await Dialogs.ShowMessageAsync(App.MainWindow, "路径不存在", $"指定的存放路径不存在：\n{text}\n\n已恢复为之前的有效路径。");
        var fallback = _originalStoragePath ?? MemeDataEngine.DefaultStoragePath();
        _revertingPath = true;
        StoragePathBox.Text = fallback;
        _revertingPath = false;
    }

    public async Task SaveAsync()
    {
        if (_saveStarted) return;
        _saveStarted = true;

        var theme = (ThemeMode)ThemeComboBox.SelectedIndex;
        double.TryParse(PreviewMaxWidthBox.Text, out double pw);
        double.TryParse(PreviewMaxHeightBox.Text, out double ph);
        int.TryParse(PreviewDelayBox.Text, out int delay);

        string? newStoragePath = null;
        bool pathChanged = false;
        var typedPath = StoragePathBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(typedPath) && Directory.Exists(typedPath))
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
            cfg.SaveLogFile = SaveLogToggle.IsChecked == true;
            cfg.EcoMode = EcoModeToggle.IsChecked == true;
            cfg.AutoStart = AutoStartToggle.IsChecked == true;
            cfg.UseControlReuse = UseControlReuseToggle.IsChecked == true;
            cfg.DragOutputAsImage = DragOutputAsImageToggle.IsChecked == true;
            cfg.ExplorerStyleMultiSelect = ExplorerStyleMultiSelectToggle.IsChecked == true;
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

        if ((pw > 0 && ph > 0) || pathChanged)
            App.MainWindow.ReloadData();
        if (delay > 0)
            App.MainWindow.ApplyPreviewDelayFromConfig();

        App.MainWindow.ApplyListStrategyFromConfig();
        App.MainWindow.ReloadData();

        bool ok = AutoStartToggle.IsChecked == true ? StartupManager.Enable() : StartupManager.Disable();
        if (!ok) Logger.Log("[Settings] 设置开机自启失败（注册表写入被拒绝）");

        Logger.Log("[Settings] 配置已保存");
    }

    // “完成”按钮只负责请求关闭 Flyout；真正的保存统一在 Flyout 的 Closed 事件里执行，
    // 这样无论以何种方式关闭（完成按钮 / 代码 Hide）都能保证保存，且避免 Flyout 卸载竞态。
    private void CloseButton_Click(object? sender, RoutedEventArgs e)
        => Finished?.Invoke(this, EventArgs.Empty);
}
