using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using MemeManager.ViewModels;

namespace MemeManager;

public sealed partial class MainWindow : Window
{
    private readonly IntPtr _hWnd;
    private bool _isVisible = true;
    private const int HOTKEY_ID = 9001;
    private const uint SUBCLASS_ID = 101;

    // 必须保持对回调委托的硬引用，防止被垃圾回收器 (GC) 回收
    private readonly NativeMethods.SUBCLASSPROC _subclassProc;

    private readonly ObservableCollection<MemeViewModel> _memeList = new();

    public MainWindow()
    {
        InitializeComponent();

        _hWnd = WindowNative.GetWindowHandle(this);

        // 1. 无焦点样式置顶
        int exStyle = NativeMethods.GetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST);

        // 2. 注册 Alt + E 全局热键
        NativeMethods.RegisterHotKey(_hWnd, HOTKEY_ID, NativeMethods.MOD_ALT, 0x45);

        // 3. 挂接窗口过程
        _subclassProc = NewWindowProc;
        NativeMethods.SetWindowSubclass(_hWnd, _subclassProc, SUBCLASS_ID, IntPtr.Zero);

        // 4. 调整窗体尺寸
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(420, 600));

        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlappedPresenter)
        {
            overlappedPresenter.IsAlwaysOnTop = true;
        }

        // 5. 🎯 动态解耦挂载：绑定 GridView 的数据源与底层物理指针按下事件
        if (Content is FrameworkElement root && root.FindName("MemeGridView") is GridView gridView)
    {
        gridView.ItemsSource = _memeList;
        gridView.PointerPressed += MemeGridView_PointerPressed;
    }

        // 首次加载数据
        LoadVisibleMemes();
    }
    private IntPtr NewWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_MOUSEACTIVATE)
        {
            return (IntPtr)3; // MA_NOACTIVATE
        }

        if (uMsg == NativeMethods.WM_HOTKEY && (int)wParam == HOTKEY_ID)
        {
            ToggleWindowVisibility();
            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void LoadVisibleMemes()
    {
        _memeList.Clear();

        // 🎯 从全局单例数据引擎中获取所有表情包
        var rawMemes = App.DataEngine.GetMemes();

        foreach (var meme in rawMemes)
        {
            _memeList.Add(new MemeViewModel(meme));
        }

        // 🛠️ 首次运行调试：如果里面什么都没有，悄悄伪造一个刚才的测试图片，免得界面空空如也
        if (_memeList.Count == 0)
        {
            string testImg = @"C:\Users\Admin\Pictures\稍有不慎就能写出更多bug来.jpg";
            if (File.Exists(testImg))
            {
                _ = App.DataEngine.ImportMemeAsync(testImg, "程序员专属", new() { "bug", "测试" }).ContinueWith(t =>
                {
                    if (t.Result != null)
                    {
                        DispatcherQueue.TryEnqueue(() => _memeList.Add(new MemeViewModel(t.Result)));
                    }
                });
            }
        }
    }

    // 🎯 降维打击的核心点击：直接在 GridView 的全局容器上捕捉底层输入指针
    private async void MemeGridView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 1. 获取当前鼠标点中的视觉原件 (OriginalSource)
        var sourceElement = e.OriginalSource as FrameworkElement;
        if (sourceElement == null) return;

        // 2. 向上追溯，看看点击的是不是某个表情包的 DataContext
        if (sourceElement.DataContext is MemeViewModel clickedMeme)
        {
            // 3. 抓到对应的路径，立刻触发异步剪贴板穿透投递！
            await PasteService.OutputMemeToCursorAsync(clickedMeme.LocalPath);

            // 4. 递增使用频次（用于热度统计）
            await App.DataEngine.IncrementUsageAsync(clickedMeme.Hash);
        }
    }
    
    private void Window_Closed(object sender, WindowEventArgs args)
    {
        NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_ID);
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


    private async void TestButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // 🛠️ 临时测试：请先换成你本地电脑里一张真实存在的图片绝对路径
        string testImagePath = @"C:\Users\Admin\Pictures\稍有不慎就能写出更多bug来.jpg";
        await PasteService.OutputMemeToCursorAsync(testImagePath);
    }
}

