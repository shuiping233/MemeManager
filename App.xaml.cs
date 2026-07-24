using Microsoft.UI.Xaml;
using MemeManager.Models;
using Microsoft.UI.Xaml.Controls;
using MemeManager.Data;
using MemeManager.Helpers;
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
            // 已有实例：读取本进程写入的实例锁文件（含旧窗口 HWND + PID），
            // 向旧窗口投递“呼出”消息，由旧实例自己激活自身（绕过前台锁定），随后静默退出。
            // 重复判断不依赖该文件（靠 mutex），文件仅用于精准定位旧窗口；
            // 即使进程被强杀导致文件残留/失效，也只会影响本次呼出，不会误判或无法启动。
            var (hwnd, pid) = ReadInstanceLock();
            if (hwnd != IntPtr.Zero)
            {
                // PID 校验：避免 HWND 被系统复用给无关窗口时误唤醒
                uint ownerPid;
                NativeMethods.GetWindowThreadProcessId(hwnd, out ownerPid);
                if (ownerPid == pid && NativeMethods.IsWindow(hwnd))
                {
                    uint showMsg = NativeMethods.RegisterWindowMessageW("MemeManager_ShowExisting");
                    NativeMethods.PostMessage(hwnd, showMsg, IntPtr.Zero, IntPtr.Zero);
                }
            }
            Logger.Log("[MemeManager] 检测到重复实例，已请求旧实例呼出并退出");
            _singleInstanceMutex?.Close();
            Current.Exit();
            return;
        }

        await Localization.InitializeAsync();

        await DataEngine.InitializeAsync();

        Logger.Log($"[EcoQos] 效率模式配置: {(DataEngine.Config.EcoMode ? "启用" : "关闭")}");
        EcoQos.ApplyProcessLevelFromConfig();

        // 配置读取完毕后立即应用语言：首次启动跟随系统，否则用配置值。
        // 必须在创建主窗口前完成，使主窗口一出来就是正确文案。
        LangHelper.ApplyConfiguredLanguage();

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

    // 实例锁文件：记录当前运行实例的主窗口 HWND 与 PID，供重复启动的新实例精准呼出旧窗口。
    // 与 config.json 同目录（%LOCALAPPDATA%\MemeManager）。
    private static string InstanceLockPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemeManager", "instance.lock");

    // 读取实例锁文件，返回 (HWND, PID)。文件不存在/格式错误时返回 (Zero, 0)。
    private static (IntPtr hwnd, uint pid) ReadInstanceLock()
    {
        try
        {
            if (!File.Exists(InstanceLockPath)) return (IntPtr.Zero, 0);
            var lines = File.ReadAllLines(InstanceLockPath);
            if (lines.Length < 2) return (IntPtr.Zero, 0);
            if (!long.TryParse(lines[0], out long hwndVal)) return (IntPtr.Zero, 0);
            if (!uint.TryParse(lines[1], out uint pid)) return (IntPtr.Zero, 0);
            return ((IntPtr)hwndVal, pid);
        }
        catch
        {
            return (IntPtr.Zero, 0);
        }
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

        // 自定义标题栏（含系统最小/最大/关闭按钮）同步按主题上色。
        try { MainWindow.ApplyTitleBarTheme(); } catch { }
    }
}
