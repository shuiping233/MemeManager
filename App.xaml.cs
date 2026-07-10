using Microsoft.UI.Xaml;
using MemeManager.Models;
using Microsoft.UI.Xaml.Controls;
using MemeManager.Data;
using WinRT.Interop;
using System.Runtime.InteropServices;

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
            await DataEngine.AddCategoryAsync("Default");
        }

        _window = new MainWindow();
        ApplyTheme();
        if (DataEngine.Config.EcoMode)
            ApplyEcoMode();
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
        MainWindow.RequestExit();
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

    public static void ApplyEcoMode()
    {
        var ok = SetThrottling(on: true);
        System.Diagnostics.Debug.WriteLine($"[MemeManager.Eco] 启用效率模式: {(ok ? "成功" : "失败")}");
    }

    public static void ResetEcoMode()
    {
        var ok = SetThrottling(on: false);
        System.Diagnostics.Debug.WriteLine($"[MemeManager.Eco] 关闭效率模式: {(ok ? "成功" : "失败")}");
    }

    // 通过 SetProcessInformation(ProcessPowerThrottling) 启用/关闭 Windows 效率模式。
    // 同时下调优先级类到 BELOW_NORMAL，任务管理器才会显示“效率模式”小绿叶。
    // 不同 Windows 版本中 ProcessPowerThrottling 的 class 值不同（Win11 24H2=4，旧版=29），依次尝试。
    private static bool SetThrottling(bool on)
    {
        try
        {
            var handle = NativeMethods.GetCurrentProcess();

            // 1) 设置执行速度节流
            var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = on ? NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
            };

            var size = (uint)Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>();
            var ptr = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.StructureToPtr(state, ptr, false);
                foreach (var cls in new[] { 4, 29 })
                {
                    if (NativeMethods.SetProcessInformation(handle, cls, ptr, size))
                        break;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            // 2) 调整优先级类：启用时降到 BELOW_NORMAL，关闭时恢复 NORMAL
            NativeMethods.SetPriorityClass(
                handle,
                on ? NativeMethods.BELOW_NORMAL_PRIORITY_CLASS : NativeMethods.NORMAL_PRIORITY_CLASS);

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemeManager.Eco] 设置效率模式异常: {ex}");
            return false;
        }
    }
}
