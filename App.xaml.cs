using Microsoft.UI.Xaml;
using MemeManager.Models;
using Microsoft.UI.Xaml.Controls;
using MemeManager.Data;
using WinRT.Interop;

namespace MemeManager;

public partial class App : Application
{
    private Window? _window;
    private TrayIcon? _trayIcon;

    public static MemeDataEngine DataEngine { get; } = new();

    public static MainWindow MainWindow => ((App)Current)._window as MainWindow
        ?? throw new System.InvalidOperationException("MainWindow 尚未初始化");

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        await DataEngine.InitializeAsync();

        // 若没有任何分类，则初始化一个默认分类，避免界面空荡
        if (DataEngine.GetCategories().Count == 0)
        {
            await DataEngine.AddCategoryAsync("默认");
        }

        _window = new MainWindow();
        ApplyTheme();
        _window.Activate();

        // 系统托盘图标
        _trayIcon = new TrayIcon(WindowNativeHwnd());
        _trayIcon.ShowMainWindow += (_, _) => MainWindow.ShowAndActivate();
        _trayIcon.OpenSettings += (_, _) => MainWindow.OpenSettings();
        _trayIcon.ExitApplication += (_, _) => ExitApp();
    }

    private static IntPtr WindowNativeHwnd()
    {
        return WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
    }

    private void ExitApp()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        _window?.Close();
        Current.Exit();
    }

    public static void ApplyTheme()
    {
        if (MainWindow is null) return;
        var theme = DataEngine.Config.Theme;
        ElementTheme applied = theme switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        if (MainWindow.Content is FrameworkElement root)
            root.RequestedTheme = applied;
    }
}
