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
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CloseButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 进入设置页即聚焦，确保能接收 Enter 键
        this.Focus(FocusState.Programmatic);
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

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveAsync();

        if (Parent is Flyout flyout)
            flyout.Hide();
    }
}
