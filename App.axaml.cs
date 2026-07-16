using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MemeManager.Data;
using MemeManager.Models;
using System.Linq;
using System.Threading;

namespace MemeManager
{
    public partial class App : Application
    {
        private static Mutex? _singleInstanceMutex;
        private TrayIcon? _trayIcon;

        public static MemeDataEngine DataEngine { get; } = new();

        public static MainWindow MainWindow =>
            ((App)Current)._window ?? throw new System.InvalidOperationException("MainWindow 尚未初始化");
        private MainWindow? _window;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// 尝试获取单实例互斥锁。若已有实例在运行，会激活其窗口、提示用户并返回 false。
        /// 必须在 Avalonia 启动（进入消息循环）之前调用，避免在 OnFrameworkInitializationCompleted
        /// 内调用 desktop.Shutdown() 触发 "Dispatcher shut down" 异常。
        /// </summary>
        public static bool TryAcquireSingleInstance()
        {
            const string mutexName = @"Global\MemeManager_SingleInstance";
            bool createdNew;
            try
            {
                _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);
            }
            catch (System.UnauthorizedAccessException)
            {
                createdNew = false;
                _singleInstanceMutex = null;
            }

            if (!createdNew)
            {
                var existing = NativeMethods.FindWindowW(null, "MemeManager");
                if (existing != System.IntPtr.Zero)
                {
                    if (NativeMethods.IsIconic(existing))
                        NativeMethods.ShowWindow(existing, 9);
                    NativeMethods.SetForegroundWindow(existing);
                }
                NativeMethods.MessageBoxW(System.IntPtr.Zero, "MemeManager 已经在运行中。", "提示", 0x0);
                _singleInstanceMutex?.Close();
                _singleInstanceMutex = null;
                return false;
            }
            return true;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _ = DataEngine.InitializeAsync().ContinueWith(_ =>
                {
                    if (DataEngine.GetCategories().Count == 0)
                        _ = DataEngine.AddCategoryAsync("Default");
                });

                _window = new MainWindow();
                desktop.MainWindow = _window;

                var cmdArgs = System.Environment.GetCommandLineArgs();
                if (cmdArgs.Contains(StartupManager.LaunchArgs, System.StringComparer.OrdinalIgnoreCase))
                    _window.StartHidden();
                else
                    _window.ShowWithoutActivate();

                var hwnd = _window.TryGetPlatformHandle()?.Handle ?? System.IntPtr.Zero;
                _trayIcon = new TrayIcon(hwnd);
                _trayIcon.ShowMainWindow += (_, _) => MainWindow.ShowAndActivate();
                _trayIcon.OpenSettings += (_, _) => MainWindow.OpenSettings();
                _trayIcon.ExitApplication += (_, _) => ExitApp();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void ExitApp()
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            MainWindow.RequestExit();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }

        public static void ApplyTheme()
        {
            if (MainWindow is null) return;
            var theme = DataEngine.Config.Theme;
            MainWindow.RequestedThemeVariant = theme switch
            {
                ThemeMode.Light => ThemeVariant.Light,
                ThemeMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
    }
}
