using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MemeManager.Models;
using Windows.Storage.Pickers;

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
        var picker = new FolderPicker();
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            StoragePathBox.Text = folder.Path;
        }
    }

    public async Task SaveAsync()
    {
        var theme = (ThemeMode)ThemeComboBox.SelectedIndex;
        var path = StoragePathBox.Text;

        await App.DataEngine.UpdateConfigAsync(cfg =>
        {
            cfg.Theme = theme;
            cfg.StoragePath = path;
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
