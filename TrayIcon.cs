using System;
using System.Runtime.InteropServices;
using System.Text;
using MemeManager.Data;

namespace MemeManager;

// 系统托盘图标：右键弹出「显示主窗口 / 设置 / 退出」
public sealed class TrayIcon : IDisposable
{
    private const int WM_TRAYICON = 0x8000 + 1; // 自定义回调消息
    private const int WM_TASKBAR_CREATED = 0x8000 + 2;
    private const uint TRAY_ICON_ID = 9002;

    private static readonly Guid _taskbarCreatedMsg = new("65E11C91-308E-4CB9-A3C9-3C7B3BAD8748");

    private readonly IntPtr _hwnd;
    private readonly NativeMethods.SUBCLASSPROC _subclassProc;
    private bool _disposed;

    // 菜单命令 ID
    private const int CMD_SHOW = 1001;
    private const int CMD_SETTINGS = 1002;
    private const int CMD_EXIT = 1003;

    public event EventHandler? ShowMainWindow;
    public event EventHandler? OpenSettings;
    public event EventHandler? ExitApplication;

    public TrayIcon(IntPtr ownerHwnd)
    {
        _hwnd = ownerHwnd;

        _subclassProc = new NativeMethods.SUBCLASSPROC(WndProc);
        NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, TRAY_ICON_ID, IntPtr.Zero);

        Register();
    }

    private void Register()
    {
        var iconPath = GetIconPath();
        var hIcon = LoadIconFromFile(iconPath);

        var data = new NativeMethods.TrayNotifyIconData
        {
            cbSize = Marshal.SizeOf<NativeMethods.TrayNotifyIconData>(),
            hWnd = _hwnd,
            uID = TRAY_ICON_ID,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = "MemeManager 表情包管理器"
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

    private void ShowContextMenu()
    {
        var hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        NativeMethods.AppendMenu(hMenu, MF_STRING, CMD_SHOW, "显示主窗口");
        NativeMethods.AppendMenu(hMenu, MF_STRING, CMD_SETTINGS, "设置");
        NativeMethods.AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);
        NativeMethods.AppendMenu(hMenu, MF_STRING, CMD_EXIT, "退出");

        NativeMethods.GetCursorPos(out var pt);
        NativeMethods.SetForegroundWindow(_hwnd);
        var cmd = NativeMethods.TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(hMenu);

        switch (cmd)
        {
            case CMD_SHOW: ShowMainWindow?.Invoke(this, EventArgs.Empty); break;
            case CMD_SETTINGS: OpenSettings?.Invoke(this, EventArgs.Empty); break;
            case CMD_EXIT: ExitApplication?.Invoke(this, EventArgs.Empty); break;
        }
    }

    private static void Log(string msg) => Logger.Log($"[MemeManager.Tray] {msg}");

    private static string GetIconPath()
    {
        // 依次尝试多个可能的位置（打包/非打包运行目录不同）
        var candidates = new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "AppX", "Assets", "AppIcon.ico"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "AppIcon.ico"),
            System.IO.Path.Combine(System.AppContext.BaseDirectory, "..", "Assets", "AppIcon.ico"),
        };
        foreach (var p in candidates)
        {
            if (System.IO.File.Exists(p)) return p;
        }
        return candidates[0];
    }

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
    private const uint TPM_RETURNCMD = 0x100;
    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const int IDI_APPLICATION = 32512;
}
