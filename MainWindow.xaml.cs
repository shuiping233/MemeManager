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

        _hWnd = WindowNative.GetWindowHandle(this);

        // 保留原本的样式注入
        int exStyle = NativeMethods.GetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST);

        NativeMethods.RegisterHotKey(_hWnd, HOTKEY_ID, NativeMethods.MOD_ALT, 0x45);

        _subclassProc = NewWindowProc;
        NativeMethods.SetWindowSubclass(_hWnd, _subclassProc, SUBCLASS_ID, IntPtr.Zero);

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(420, 600));

        // 🎯 核心新增：使用 WinUI 3 官方推荐的 OverlappedPresenter 确保绝对置顶
        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlappedPresenter)
        {
            overlappedPresenter.IsAlwaysOnTop = true;
        }
    }

    private IntPtr NewWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
    {
        // 🎯 修改点 2：拦截鼠标激活消息！
        // 当鼠标点击这个窗口时，直接回复系统：允许点击，但不允许把别的应用（如微信）的焦点抢过来！
        if (uMsg == NativeMethods.WM_MOUSEACTIVATE)
        {
            return (IntPtr)NativeMethods.MA_NOACTIVATE;
        }

        if (uMsg == NativeMethods.WM_HOTKEY && (int)wParam == HOTKEY_ID)
        {
            ToggleWindowVisibility();
            return IntPtr.Zero;
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
    private async void TestButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // 🛠️ 临时测试：请先换成你本地电脑里一张真实存在的图片绝对路径
        string testImagePath = @"C:\Users\Admin\Pictures\稍有不慎就能写出更多bug来.jpg";
        await PasteService.OutputMemeToCursorAsync(testImagePath);
    }
}

