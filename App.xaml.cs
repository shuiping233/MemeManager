using Microsoft.UI.Xaml;
using MemeManager.Models;
using Microsoft.UI.Xaml.Controls;
using MemeManager.Data;
using WinRT.Interop;
using System.IO;
using System.Threading.Tasks;

namespace MemeManager;

public partial class App : Application
{
    private Window? _window;
    private TrayIcon? _trayIcon;

    // 单实例互斥体：持有期间禁止第二个实例启动
    private static Mutex? _singleInstanceMutex;

    public static MemeDataEngine DataEngine { get; } = new();

    public static MainWindow MainWindow => ((App)Current)._window as MainWindow
        ?? throw new System.InvalidOperationException("MainWindow 尚未初始化");

    public App()
    {
        InitializeComponent();

        // 全局崩溃兜底：捕获 UI 线程 / 后台线程 / 未观察 Task 异常
        UnhandledException += (_, args) =>
        {
            args.Handled = true;
            HandleFatalException(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            HandleFatalException(ex ?? new Exception("Unknown non-CLR exception"));
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            HandleFatalException(args.Exception);
        };
    }

    // 致命异常处理：写崩溃日志 + 弹窗提示 + 退出
    private static void HandleFatalException(Exception ex)
    {
        try
        {
            var baseDir = MemeDataEngine.DefaultStoragePath();
            var logDir = Path.Combine(baseDir, "log");
            Directory.CreateDirectory(logDir);
            var crashPath = Path.Combine(logDir, "crash.log");
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CRASH] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}";
            File.AppendAllText(crashPath, msg);

            Logger.Log("[CRASH] " + ex);
        }
        catch
        {
            // 连写日志都失败就不管了，不能再次抛异常
        }

        try
        {
            NativeMethods.MessageBoxW(
                IntPtr.Zero,
                $"程序遇到意外错误，即将退出：\n\n{ex.GetType().Name}: {ex.Message}\n\n崩溃日志已保存到日志目录的 crash.log",
                "MemeManager 崩溃",
                0x10); // MB_ICONERROR | MB_OK
        }
        catch
        {
        }

        try { Current?.Exit(); } catch { }
        Environment.Exit(1);
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // ---------- 单实例检查 ----------
        const string mutexName = @"Global\MemeManager_SingleInstance";
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // 已存在但无法获取所有权，视为已有实例在运行
            createdNew = false;
            _singleInstanceMutex = null;
        }

        if (!createdNew)
        {
            // 已有实例：弹窗提示，并尝试把已有窗口激活到前台
            IntPtr existing = NativeMethods.FindWindowW(null, "MemeManager");
            if (existing != IntPtr.Zero)
            {
                if (NativeMethods.IsIconic(existing))
                    NativeMethods.ShowWindow(existing, 9); // SW_RESTORE
                NativeMethods.SetForegroundWindow(existing);
            }
            NativeMethods.MessageBoxW(
                IntPtr.Zero,
                "MemeManager 已经在运行中。",
                "提示",
                0x0); // MB_OK
            Logger.Log("[MemeManager] 检测到重复实例并退出");
            _singleInstanceMutex?.Close();
            Current.Exit();
            return;
        }

        await DataEngine.InitializeAsync();

        Logger.Log($"[EcoQos] 效率模式配置: {(DataEngine.Config.EcoMode ? "启用" : "关闭")}");
        EcoQos.ApplyProcessLevelFromConfig();

        // 若没有任何分类，则初始化一个默认分类，避免界面空荡
        if (DataEngine.GetCategories().Count == 0)
        {
            await DataEngine.AddCategoryAsync("Default");
        }

        _window = new MainWindow();
        ApplyTheme();

        // 开机自启(--hidden)：直接隐藏到后台、只留托盘，不闪界面
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Contains(StartupManager.LaunchArgs, StringComparer.OrdinalIgnoreCase))
        {
            ((MainWindow)_window).StartHidden();
        }
        else
        {
            // 普通启动：显示但抢前台焦点，让外部应用(QQ 等)保持前台，
            // 以便 _fgTimer 记录其窗口句柄，点击表情能精准投回输入框。
            ((MainWindow)_window).ShowWithoutActivate();
        }

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
        Logger.Log("[EcoQos] 应用程序退出，线程级效率模式随后台线程结束而失效");
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
        FrameworkElement? root = MainWindow.Content as FrameworkElement;
        if (root != null)
            root.RequestedTheme = applied;

        // 弹窗统一强制主题：System 时解析为当前系统实际主题（root.ActualTheme），
        // 避免 Win10/Win11 下模态弹窗主题表现不一致。
        DialogHelper.DialogTheme = theme == ThemeMode.System
            ? (root?.ActualTheme ?? ElementTheme.Default)
            : applied;
    }
}
