using System;
using System.Runtime.InteropServices;
using System.Text;
using MemeManager.Views;

namespace MemeManager.Infrastructure;

// 系统托盘图标：右键弹出「显示主窗口 / 设置 / 退出」
public sealed class TrayIcon : IDisposable
{
    private const int WM_TRAYICON = 0x8000 + 1; // 自定义回调消息
    private const int WM_TASKBAR_CREATED = 0x8000 + 2;
    private const uint TRAY_ICON_ID = 9002;

    private static readonly Guid _taskbarCreatedMsg = new("65E11C91-308E-4CB9-A3C9-3C7B3BAD8748");

    private readonly IntPtr _hwnd;
    private readonly NativeMethods.SUBCLASSPROC _subclassProc;
    private readonly MemeDataEngine _engine;
    private bool _disposed;

    // 菜单命令 ID
    private const int CMD_SHOW = 1001;
    private const int CMD_TOGGLE_MODE = 1002;
    private const int CMD_SETTINGS = 1003;
    private const int CMD_EXIT = 1004;

    public event EventHandler? ShowMainWindow;
    public event EventHandler? ToggleMode;
    public event EventHandler? OpenSettings;
    public event EventHandler? ExitApplication;

    public TrayIcon(IntPtr ownerHwnd, MemeDataEngine engine)
    {
        _hwnd = ownerHwnd;
        _engine = engine;

        _subclassProc = new NativeMethods.SUBCLASSPROC(WndProc);
        NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, TRAY_ICON_ID, IntPtr.Zero);

        Register();
    }

    private void Register()
    {
        var hIcon = LoadIconFromFile(MainWindow.AppIconPath);

        var data = new NativeMethods.TrayNotifyIconData
        {
            cbSize = Marshal.SizeOf<NativeMethods.TrayNotifyIconData>(),
            hWnd = _hwnd,
            uID = TRAY_ICON_ID,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = Localization.Get("Tray_Tooltip")
        };

        NativeMethods.Shell_NotifyIcon(NIM_ADD, ref data);
        data.uVersion = 4;
        NativeMethods.Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_TRAYICON)
        {
            var mouseMsg = (uint)lParam & 0xFFFF;
            if (mouseMsg == NativeMethods.WM_LBUTTONUP)
            {
                ShowMainWindow?.Invoke(this, EventArgs.Empty);
            }
            else if (mouseMsg == NativeMethods.WM_RBUTTONUP)
            {
                ShowContextMenu();
            }
            return IntPtr.Zero;
        }

        if (uMsg == NativeMethods.WM_DESTROY)
        {
            Remove();
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>
    /// 手动将图标设到窗口，使独立发布（非 MSIX）时任务栏/标题栏也显示 Logo。
    /// WinUI 3 不会自动从 EXE 图标继承窗口图标，需通过 WM_SETICON 显式设置。
    /// </summary>
    public static void SetTaskbarIcon(IntPtr _hWnd , IntPtr hIcon)
    {
        try
        {
            if (hIcon == IntPtr.Zero)
                return;

            // 同时设置大/小两套，任务栏用 small，标题栏/alt-tab 用 big
            NativeMethods.SendMessage(_hWnd, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_SMALL, hIcon);
            NativeMethods.SendMessage(_hWnd, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_BIG, hIcon);
        }
        catch (Exception ex)
        {
            Log($"设置窗口图标失败: {ex}");
        }
    }

    public static IntPtr LoadAppIcon(string appIconPath)
    {
        // 从 exe 运行目录的 AppIcon.ico 文件加载（LoadImage 已验证可用）
        var path = appIconPath;
        if (File.Exists(path))
        {
            var h = NativeMethods.LoadImage(
                IntPtr.Zero, path, NativeMethods.IMAGE_ICON, 0, 0,
                NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
            if (h != IntPtr.Zero)
                return h;
        }

        Log("未找到 AppIcon.ico");
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        var hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        NativeMethods.AppendMenu(hMenu, MF_STRING, CMD_SHOW, Localization.Get("Tray_Show"));

        // Mini 模式被配置禁用时，“切换窗口模式”菜单项置灰且不可选。
        bool allowMini = _engine.Config.AllowMiniMode;
        if (allowMini)
            NativeMethods.AppendMenu(hMenu, MF_STRING, CMD_TOGGLE_MODE, Localization.Get("Tray_ToggleMode"));
        else
            NativeMethods.AppendMenu(hMenu, MF_STRING | MF_GRAYED, CMD_TOGGLE_MODE, Localization.Get("Tray_ToggleMode"));

        NativeMethods.AppendMenu(hMenu, MF_STRING, CMD_SETTINGS, Localization.Get("Tray_Settings"));
        NativeMethods.AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);
        NativeMethods.AppendMenu(hMenu, MF_STRING, CMD_EXIT, Localization.Get("Tray_Exit"));

        NativeMethods.GetCursorPos(out var pt);
        NativeMethods.SetForegroundWindow(_hwnd);
        var cmd = NativeMethods.TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(hMenu);

        switch (cmd)
        {
            case CMD_SHOW: ShowMainWindow?.Invoke(this, EventArgs.Empty); break;
            case CMD_TOGGLE_MODE: if (allowMini) ToggleMode?.Invoke(this, EventArgs.Empty); break;
            case CMD_SETTINGS: OpenSettings?.Invoke(this, EventArgs.Empty); break;
            case CMD_EXIT: ExitApplication?.Invoke(this, EventArgs.Empty); break;
        }
    }

    private static void Log(string msg) => Logger.Log($"[TrayIcon] {msg}");

    private static IntPtr LoadIconFromFile(string path)
    {
        if (System.IO.File.Exists(path))
        {
            try
            {
                return NativeMethods.LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
            }
            catch { }
        }
        // 回退到系统图标
        return NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
    }

    private void Remove()
    {
        if (_disposed) return;
        var data = new NativeMethods.TrayNotifyIconData
        {
            cbSize = Marshal.SizeOf<NativeMethods.TrayNotifyIconData>(),
            hWnd = _hwnd,
            uID = TRAY_ICON_ID
        };
        NativeMethods.Shell_NotifyIcon(NIM_DELETE, ref data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Remove();
        NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, TRAY_ICON_ID);
    }

    // ---------- 常量与结构体 ----------

    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint NIM_ADD = 0x0;
    private const uint NIM_DELETE = 0x2;
    private const uint NIM_SETVERSION = 0x4;
    private const uint MF_STRING = 0x0;
    private const uint MF_SEPARATOR = 0x800;
    private const uint MF_GRAYED = 0x1;
    private const uint TPM_RETURNCMD = 0x100;
    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const int IDI_APPLICATION = 32512;
}
