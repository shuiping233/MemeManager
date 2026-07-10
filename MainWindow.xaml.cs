using Microsoft.UI.Xaml;
using System;
using WinRT.Interop;

namespace MemeManager;

public sealed partial class MainWindow : Window
{
    private readonly IntPtr _hWnd;
    private bool _isVisible = true;
    private const int HOTKEY_ID = 9001;
    private const uint SUBCLASS_ID = 101;

    // 必须保持对回调委托的硬引用，防止被垃圾回收器 (GC) 回收
    private readonly NativeMethods.SUBCLASSPROC _subclassProc;

    public MainWindow()
    {
        InitializeComponent();

        // 1. 获取 WinUI 3 窗体的底层 Win32 HWND
        _hWnd = WindowNative.GetWindowHandle(this);

        // 2. 强行注入“不激活窗口 (无焦点)” + “永远置顶” 样式
        int exStyle = NativeMethods.GetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST);

        // 3. 注册全局热键：Alt + E (E 的虚拟键码是 0x45)
        NativeMethods.RegisterHotKey(_hWnd, HOTKEY_ID, NativeMethods.MOD_ALT, 0x45);

        // 4. 挂接窗口子类化过程，用于在后台拦截热键消息
        _subclassProc = NewWindowProc;
        NativeMethods.SetWindowSubclass(_hWnd, _subclassProc, SUBCLASS_ID, IntPtr.Zero);

        // 5. 设置初始大小（例如仿系统 Emoji 面板的紧凑尺寸）
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(420, 600));
    }

    // 这就是我们接管的 WndProc 子消息循环，纯静态编译安全，AOT 绝不报错
    private IntPtr NewWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_HOTKEY && (int)wParam == HOTKEY_ID)
        {
            ToggleWindowVisibility();
            return IntPtr.Zero; // 消息已处理
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ToggleWindowVisibility()
    {
        if (_isVisible)
        {
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_HIDE);
            ReleaseUiResources();
        }
        else
        {
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_SHOW);
            LoadVisibleMemes();
        }
        _isVisible = !_isVisible;
    }

    private void ReleaseUiResources()
    {
        // 留空：后续在这里清空 GridView 数据源并执行 GC 压榨内存
    }

    private void LoadVisibleMemes()
    {
        // 留空：后续在这里异步重新拉取当前分类的 JSON 元数据
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        // 程序关闭时清理热键
        NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_ID);
    }
}