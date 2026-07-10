using System;
using System.Runtime.InteropServices;

namespace MemeManager;

internal static partial class NativeMethods
{
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3; // 不激活窗口，但吃掉/处理鼠标消息

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    public const int WM_HOTKEY = 0x0312;
    public const int MOD_ALT = 0x0001;

    public const int WM_ACTIVATE = 0x0006;
    public const int WA_INACTIVE = 0;
    public const int WA_ACTIVE = 1;
    public const int WA_CLICKACTIVE = 2;

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_SHOWNOACTIVATE = 4;

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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    // 引入窗口子类化 API
    [LibraryImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

    [LibraryImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    public static partial IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    // --- 键盘输入模拟相关定义 ---
    public const int INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo; // 8 字节
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo; // 8 字节
    }

    // 🎯 核心修复 1：明确指定显式布局与偏移量，完美对应 x64 架构
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    // 🎯 核心修复 2：外层结构体必须使用 Explicit。
    // 在 x64 操作系统中，type 字段占用 4 字节，但由于后面的 Union 包含 8 字节指针（dwExtraInfo），
    // 导致 Union 的起始地址必须在第 8 字节处对齐（即前面产生 4 字节的空白填充 padding）。
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        [FieldOffset(0)]
        public uint type;

        [FieldOffset(8)] // 👈 跳过 4 字节 padding，从第 8 字节精确开始
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}