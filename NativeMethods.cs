using System;
using System.Runtime.InteropServices;

namespace MemeManager;

internal static partial class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    public const int WM_HOTKEY = 0x0312;
    public const int MOD_ALT = 0x0001;

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

    // 子类化窗口的回调委托声明
    public delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // 引入窗口子类化 API
    [LibraryImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

    [LibraryImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    public static partial IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}