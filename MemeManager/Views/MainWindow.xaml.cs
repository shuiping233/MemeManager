using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;
using MemeManager.Infrastructure;
using MemeManager.Models;

namespace MemeManager.Views;

// 窗口宿主：只负责 Window 生命周期、Win32 交互（热键/托盘呼出/置顶/拖入消息）、
// 窗口尺寸与 Full/Mini 模式切换。具体业务 UI 在 RootFrame 承载的 Page 中
// （MainPage=完整管理模式，MiniPage=紧凑快速调用模式）。
public sealed partial class MainWindow : Window
{
    private readonly IntPtr _hWnd;
    private Microsoft.UI.Windowing.AppWindow? _appWindow;
    private bool _isVisible = true;
    private const int HOTKEY_ID = 9001;
    private const uint SUBCLASS_ID = 101;

    // Mini 模式窗口尺寸（DIP，实际 Resize 时按 DPI 缩放），方便以后调整
    public const int MiniModeWidth = 280;
    public const int MiniModeHeight = 100;

    private readonly NativeMethods.SUBCLASSPROC _subclassProc;

    // 最小化结束事件钩子：窗口从最小化恢复时重新断言置顶（防止 DWM 抽风掉置顶）
    private readonly NativeMethods.WinEventProc _winEventProc;
    private IntPtr _winEventHook;

    // 当前置顶状态（会话内有效，启动默认置顶，不持久化到 config）
    private bool _topMost = true;

    // 记录本窗口激活前的前台窗口（通常是正在聊天的目标应用），用于粘贴时回投 Ctrl+V
    private IntPtr _prevActiveHwnd;
    private IntPtr _lastExternalFg;
    private bool _isActive;
    private DispatcherTimer? _fgTimer;

    // 文件选择器（FolderPicker/FileOpenPicker）打开期间：屏蔽悬停预览浮窗，
    // 避免对话框抢焦点后背后图片误弹浮窗。
    public bool IsFilePickerOpen { get; internal set; }

    // 是否允许真正关闭窗口（仅托盘“退出”时置 true；普通点 X 只隐藏）
    private bool _allowClose;

    // 窗口正在关闭/销毁中：所有异步回调(XAML 操作)据此放弃触碰控件，
    // 避免 WinUI 在视觉树销毁后仍被访问导致 native AV(0xc0000005)。
    private bool _isClosing;

    // 当前 UI 模式（Full/Mini）
    private AppMode _currentMode = AppMode.Full;
    public AppMode CurrentMode => _currentMode;

    // Mini 模式无边框：是否已把内容扩展到标题栏区域（仅 Mini 期间为 true）
    private bool _titleBarExtended;

    // ---------- 供 Page 层访问的窗口级状态 ----------

    public bool IsClosing => _isClosing;
    public bool IsAppVisible => _isVisible;
    public bool IsWindowActive => _isActive;
    public bool IsTopMost => _topMost;

    // 当前承载的完整模式页面（未处于 Full 模式或尚未导航时为 null）
    public MainPage? CurrentMainPage => RootFrame.Content as MainPage;

    // 当前承载的 Mini 模式页面（未处于 Mini 模式或尚未导航时为 null）
    public MiniPage? CurrentMiniPage => RootFrame.Content as MiniPage;

    // 当前承载的页面（两种模式皆可），用于统一驱动图像资源释放。
    private IImageReleasablePage? CurrentReleasablePage => RootFrame.Content as IImageReleasablePage;

    // 释放当前页面持有的图像资源引用，并统一执行一次 GC 回收（两种模式共用）。
    private void ReleaseCurrentPageImages()
    {
        CurrentReleasablePage?.ReleaseImages();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    public MainWindow()
    {
        InitializeComponent();

        Title = "MemeManager " + GetInformationalVersion();

        _hWnd = WindowNative.GetWindowHandle(this);

        // 写入实例锁文件（HWND + PID），供重复启动的新实例精准呼出旧窗口
        PersistInstanceLock();

        SetTaskbarIcon();

        int exStyle = NativeMethods.GetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE);
        // 启动默认置顶：始终加 TOPMOST 扩展样式（用户可在会话内手动关闭）
        NativeMethods.SetWindowLongW(_hWnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOPMOST);

        RegisterConfiguredHotKey();

        _subclassProc = NewWindowProc;
        NativeMethods.SetWindowSubclass(_hWnd, _subclassProc, SUBCLASS_ID, IntPtr.Zero);

        // 挂钩 EVENT_SYSTEM_MINIMIZEEND：本进程窗口最小化结束后重新断言置顶，
        // 弥补 WM_SYSCOMMAND 覆盖不到的真实最小化-恢复路径（参考 PowerToys）。
        _winEventProc = new NativeMethods.WinEventProc(WinEventCallback);
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero,
            _winEventProc,
            NativeMethods.GetCurrentProcessId(),
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        // 让窗口在 Win32 层面也能接收拖入的文件（QQ 等来源可能只发文件，不走 XAML DataPackage）
        NativeMethods.DragAcceptFiles(_hWnd, true);

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        if (_appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlappedPresenter)
        {
            // 启动默认置顶
            overlappedPresenter.IsAlwaysOnTop = true;
        }

        // 两种模式都保持 ExtendsContentIntoTitleBar=true（永不切回 false）：
        // WinUI 3 中把扩展从 true 切回 false 会让系统标题栏丢失 Mica 与深色按钮且无法恢复。
        // Full 用一条独立空拖拽条作标题栏（见 MainPage 顶部 TitleStrip），Mini 用 DragBar，
        // 二者都复用扩展路径，Mica 与深色按钮表现一致。
        this.ExtendsContentIntoTitleBar = true;
        _titleBarExtended = true;

        // 标题栏主题色（简单自定义）：按主题给系统默认标题栏上色。
        ApplyTitleBarTheme();

        Closed += Window_Closed;

        // 键盘事件挂在内容根（与重构前 root 一致）：它是 Page 的祖先的祖先，
        // 无论焦点在 GridView 内部多深，按键都会冒泡到此处，转发给当前页面处理
        // （Ctrl+V 导入、Esc/Enter 多选等）。注意不能只挂在 RootFrame 上，
        // GridView 内部焦点时 KeyRoutedEventArgs 无法稳定冒泡到 Frame。
        RootContainer.KeyDown += (_, e) => CurrentMainPage?.HandleHostKeyDown(e);

        // 轮询前台窗口的定时器：用于把 Ctrl+V 投回用户正在用的外部窗口(如 QQ)。
        // 只在窗口可见时运行；窗口隐藏(后台常驻)时停止，
        // 既保证粘贴目标正确，又让后台零轮询、零 CPU 占用。
        _fgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _fgTimer.Tick += (_, _) =>
        {
            if (!_isActive)
            {
                var fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != _hWnd)
                    _lastExternalFg = fg;
            }
        };
    }

    private static string GetInformationalVersion()
    {
        var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)System.Reflection
            .Assembly
            .GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault();
        var v = attr?.InformationalVersion ?? string.Empty;
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    private static void Log(string msg) => Logger.Log($"[MemeManager] {msg}");

    // ---------- Full / Mini 模式切换 ----------

    // 启动时由 App.OnLaunched 调用：按 config 恢复上次退出时的模式。
    // 必须在 App._window 赋值之后调用（Page 构造中会访问 App.MainWindow）。
    public void InitializeMode()
    {
        // 若 config 关闭了 Mini 模式，则不恢复 Mini（强制 Full）。
        var mode = App.DataEngine.Config.AllowMiniMode
            ? App.DataEngine.Config.LastAppMode
            : AppMode.Full;
        SwitchMode(mode, persist: false);
    }

    public void SwitchMode(AppMode mode, bool persist = true)
    {
        // 配置不允许 Mini 模式时，任何进入 Mini 的请求都强制转为 Full。
        if (mode == AppMode.Mini && !App.DataEngine.Config.AllowMiniMode)
            mode = AppMode.Full;

        if (RootFrame.Content != null && _currentMode == mode) return;

        // 离开 Full 模式前记录窗口尺寸，切回时据此还原（_currentMode 仍为 Full 时才会真正写入）
        if (mode == AppMode.Mini && RootFrame.Content is MainPage)
            SaveWindowSize();

        var prevMode = _currentMode;
        _currentMode = mode;

        // 切模式前释放“即将被替换”的旧页面的图像资源（仅断 VM/Image 引用，不置空
        // ItemsSource——分类容器本就无需置空，且导航会卸载旧页面整棵可视化树；
        // 在此处手动 null ItemsSource 反而会在导航前扰乱状态，导致切回的页面空白）。
        ReleaseCurrentPageImages();

        switch (mode)
        {
            case AppMode.Full:
                RootFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
                RestoreFullModeChrome();
                ResizeForFullMode();
                // 从 Mini 切回：恢复内存中的置顶状态（Mini 期间是强制置顶的，不改写 _topMost）。
                ApplyTopMost(_topMost);
                // 从 Mini 切回：刷新分类与图片容器，确保与磁盘数据同步（Mini 期间可能导入过表情）。
                // 仅在 Mini→Full 时刷新；首次启动由 MainPage 构造函数加载，避免重复 RefreshMemes
                // 导致 WinUI 3 GridView 虚拟化器状态崩溃（视口外图片透明不渲染）。
                if (prevMode == AppMode.Mini)
                    DispatcherQueue.TryEnqueue(() => CurrentMainPage?.ReloadData());
                break;

            case AppMode.Mini:
                RootFrame.Navigate(typeof(MiniPage), null, new SuppressNavigationTransitionInfo());
                ResizeForMiniMode();
                ApplyTopMost(_topMost);
                break;
        }
        Log($"[模式] 已切换到 {mode}");

        if (persist)
            _ = App.DataEngine.UpdateConfigAsync(c => c.LastAppMode = mode);
    }

    // Full 模式：还原 config.json 中上次保存的宽高；无有效值则用默认。
    private void ResizeForFullMode()
    {
        RestoreWindowSize();
    }

    // Mini 模式：固定紧凑尺寸（常量为 DIP，按窗口当前 DPI 换算为物理像素）。
    // 采用“自定义标题栏”方案：保留系统边框，扩展内容到标题栏区域，并由 MiniPage
    // 通过 SetTitleBarElement 注册顶栏为标题栏 —— 系统据此让该区域可拖、且内部按钮可点，
    // 无需手挖洞（这是 WinUI 3 无边框/自定义标题栏的官方做法，依赖标题栏存在）。
    private void ResizeForMiniMode()
    {
        if (_appWindow == null) return;

        // 最大化状态下 Resize 无效且会导致窗口异常定位到屏幕左上角，
        // 先用 WinUI 3 原生 API 退出最大化。
        if (_appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op
            && op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
        {
            op.Restore();
        }

        // 扩展内容到标题栏区域（Mini 用自定义顶栏）。扩展标志全程保持 true（启动时已设），此处幂等。
        if (!_titleBarExtended)
        {
            this.ExtendsContentIntoTitleBar = true;
            _titleBarExtended = true;
        }
        if (_appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
        {
            // 固定尺寸不可缩放；最小化/最大化按钮去掉（最小化不需要，
            // 最大化由 MiniPage 的“展开”按钮替代为切回 Full 模式）。
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.SetBorderAndTitleBar(true, true);
        }

        double scale = NativeMethods.GetDpiForWindow(_hWnd) / 96.0;
        if (scale <= 0) scale = 1.0;
        int w = (int)Math.Round(MiniModeWidth * scale);
        int h = (int)Math.Round(MiniModeHeight * scale);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        Log($"[窗口] Mini 模式尺寸 {MiniModeWidth}x{MiniModeHeight} DIP -> {w}x{h} px (scale={scale:F2})");
    }

    // 切回 Full 模式：恢复可调整/有标题栏的标准窗口外观。
    // 扩展标志保持 true（永不切回 false，否则系统标题栏丢 Mica/深色按钮）；Full 用独立 TitleStrip 作标题栏，
    // 由 MainPage 在 Loaded 时注册。这里仅恢复窗口可调整/三键状态，并重设标题栏主题（深色按钮）。
    private void RestoreFullModeChrome()
    {
        if (_appWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
        {
            p.IsResizable = true;
            p.IsMaximizable = true;
            p.IsMinimizable = true;
            p.SetBorderAndTitleBar(true, true);
        }
        ApplyTitleBarTheme();
    }

    /// <summary>
    /// 把指定元素注册为窗口标题栏区域（Window.SetTitleBar）。
    /// 注册后该元素区域可拖拽窗口，且内部交互控件（按钮/下拉）仍正常点击。
    /// 目前仅 Mini 模式使用（注册 DragBar）；Full 模式用系统标题栏，不注册。
    /// 传 null 取消注册。
    /// </summary>
    public void SetTitleBarElement(Microsoft.UI.Xaml.UIElement? titleBar)
    {
        try
        {
            this.SetTitleBar(titleBar);
        }
        catch (Exception ex)
        {
            Logger.Log("[窗口] SetTitleBarElement 失败: " + ex.Message);
        }
    }

    // ---------- 窗口尺寸持久化 ----------

    // 启动/切回 Full 模式时还原：读 config.json 中上次保存的宽高。最大化状态不持久化。
    private void RestoreWindowSize()
    {
        if (_appWindow == null) return;
        var cfg = App.DataEngine.Config;

        int w = (int)Math.Max(400, cfg.WindowWidth);
        int h = (int)Math.Max(300, cfg.WindowHeight);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        Log($"[窗口] 还原尺寸 {w}x{h} (预设={cfg.WindowSizePreset})");
    }

    // 退出/关闭/切到 Mini 前保存：记录当前尺寸到 config.json。
    // 仅在 Full 模式下记录（Mini 的固定小尺寸不应覆盖完整模式的窗口尺寸）；
    // 最大化状态下也不记录（最大化置顶窗口会挡住托盘右键菜单，且还原时尺寸无意义）。
    private void SaveWindowSize()
    {
        if (_appWindow == null) return;
        if (_currentMode != AppMode.Full)
        {
            Log("[窗口] 当前为 Mini 模式，跳过尺寸记录");
            return;
        }
        var cfg = App.DataEngine.Config;

        bool maximized = _appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op && op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        if (maximized)
        {
            Log("[窗口] 当前为最大化，跳过尺寸记录");
            return;
        }

        var bounds = _appWindow.Size;
        cfg.WindowWidth = bounds.Width;
        cfg.WindowHeight = bounds.Height;
        cfg.WindowMaximized = false;
        cfg.WindowSizePreset = ClassifySize(bounds.Width, bounds.Height);
        Log($"[窗口] 保存尺寸 {bounds.Width}x{bounds.Height} (预设={cfg.WindowSizePreset})");

        _ = App.DataEngine.SaveConfigAsync();
    }

    // 依据宽高映射到最接近的尺寸预设档位（仅用于日志/调试展示）
    private static WindowSizePreset ClassifySize(int w, int h)
    {
        return (w, h) switch
        {
            (<= 800, <= 620) => WindowSizePreset.Small,
            (>= 1150, >= 880) => WindowSizePreset.Large,
            _ => WindowSizePreset.Medium
        };
    }

    // ---------- 供 Page 层调用的窗口级服务 ----------

    // 解析“点击表情后应把 Ctrl+V 投回哪个外部窗口”。
    // 目标窗口优先级：轮询记录的外部前台窗口(_lastExternalFg) >
    // 失去激活时记录的上一个外部窗口(_prevActiveHwnd) > 实时前台窗口(liveFg)。
    // _lastExternalFg 由 _fgTimer 在窗口可见且未激活时持续刷新。
    // liveFg 取“点击瞬间”的前台窗口：Tapped 事件通常先于窗口激活完成触发，
    // 此时前台往往仍是用户正在用的外部输入框(QQ 等)，故可作兜底。
    //
    // 注意 Mini 模式特例：点 Picker 图片时本窗口已是前台（点击使其激活），
    // 此时 liveFg==_hWnd，而 _lastExternalFg 也可能已被刷成自己。若最终解析到
    // 自身，则回退到 _prevActiveHwnd（本窗口获得焦点前的前台=用户正在用的外部应用），
    // 这样 Mini 点图也能正确回贴到外部应用，而不是把图“粘贴”回自己身上（看起来没反应）。
    public IntPtr ResolveExternalPasteTarget()
    {
        IntPtr liveFg = NativeMethods.GetForegroundWindow();
        IntPtr target = IntPtr.Zero;
        if (_lastExternalFg != IntPtr.Zero && _lastExternalFg != _hWnd)
            target = _lastExternalFg;
        else if (_prevActiveHwnd != IntPtr.Zero && _prevActiveHwnd != _hWnd)
            target = _prevActiveHwnd;
        else if (liveFg != IntPtr.Zero && liveFg != _hWnd)
            target = liveFg;

        // 解析到自身（或无有效目标）：回退到“获得焦点前的前台窗口”，再不行才返回 Zero。
        if (target == IntPtr.Zero || target == _hWnd)
        {
            if (_prevActiveHwnd != IntPtr.Zero && _prevActiveHwnd != _hWnd)
                return _prevActiveHwnd;
            return IntPtr.Zero;
        }
        return target;
    }

    // 取得主窗口所在屏幕的工作区，转换为【窗口坐标(DIP)】，
    // 用于把预览浮窗限制在屏幕内（而非限制在窗口内——浮窗可越过主窗口边界，只要不超出屏幕）。
    public Windows.Foundation.Rect GetWorkAreaInWindowCoords()
    {
        // 默认兜底：相对窗口的“无限”区域（即不限制），避免窗口位置取不到时把浮窗夹死。
        var fallback = new Windows.Foundation.Rect(
            -this.Bounds.X, -this.Bounds.Y,
            double.PositiveInfinity, double.PositiveInfinity);
        try
        {
            var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd),
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            if (display != null && _appWindow != null)
            {
                var r = display.WorkArea;          // 屏幕坐标
                var pos = _appWindow.Position;      // 窗口左上角屏幕坐标
                return new Windows.Foundation.Rect(
                    r.X - pos.X, r.Y - pos.Y, r.Width, r.Height);
            }
        }
        catch { }
        return fallback;
    }

    // 置顶开关（由页面上的置顶按钮/托盘等调用），仅会话内存有效，不持久化。
    public void SetTopMost(bool topMost)
    {
        _topMost = topMost;
        ApplyTopMost(topMost);
    }

    // ---------- 供 SettingsPage 调用的转发（保持原有调用面不变） ----------

    /// <summary>供设置页在“浏览”修改存放路径后即时刷新主界面（分类/表情）</summary>
    public void ReloadData() => CurrentMainPage?.ReloadData();

    public void ApplyPreviewDelayFromConfig() => CurrentMainPage?.ApplyPreviewDelayFromConfig();

    public void ApplyListStrategyFromConfig() => CurrentMainPage?.ApplyListStrategyFromConfig();

    // ---------- 显示 / 隐藏 ----------

    /// <summary>托盘菜单“设置”：先呼出窗口（Mini 模式则切回 Full），再弹设置页</summary>
    public void OpenSettings()
    {
        ShowWindow(activate: true);
        if (_currentMode != AppMode.Full)
            SwitchMode(AppMode.Full);
        DispatcherQueue.TryEnqueue(() => CurrentMainPage?.OpenSettingsFlyout());
    }

    /// <summary>托盘菜单“显示主窗口”：显示并激活窗口（兼容最小化状态）</summary>
    public void ShowAndActivate()
    {
        // 焦点重置已在 ShowWindow 内统一处理（覆盖所有显示入口），此处无需重复。
        ShowWindow(activate: true);
    }

    // 托盘菜单“切换窗口模式”：主窗口已在前台显示则直接切换；否则先呼出到前台再切换。
    // 若 config 关闭了 Mini 模式，则该菜单项应已禁用，这里再兜底拦截。
    public void ToggleMode()
    {
        if (!App.DataEngine.Config.AllowMiniMode)
            return;
        bool foreground = Visible && NativeMethods.GetForegroundWindow() == _hWnd;
        if (!foreground)
            ShowAndActivate();
        SwitchMode(_currentMode == AppMode.Full ? AppMode.Mini : AppMode.Full);
    }

    // 将当前主窗口 HWND + PID 写入实例锁文件，供重复启动的新实例精准呼出。
    // 每次拿到（新）HWND 都覆盖写入，窗口重建后也能保持最新。
    private void PersistInstanceLock()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            File.WriteAllText(InstanceLockPath,
                $"{(long)_hWnd}\n{pid}");
        }
        catch (Exception ex)
        {
            Logger.Log($"[实例锁] 写入失败: {ex.Message}");
        }
    }

    // 退出时删除实例锁文件（强杀残留也无妨，重复判断不依赖它）
    private static void DeleteInstanceLock()
    {
        try
        {
            var path = InstanceLockPath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>
    /// 统一“显示主窗口”入口：所有呼出窗口的路径都必须走这里，
    /// 以保证隐藏时停用的拖拽/交互能力被一并恢复（避免从托盘呼出后无法拖拽）。
    /// activate=true 时抢前台焦点（托盘/设置呼出），false 时不抢焦点（快捷键/普通启动，
    /// 保留外部输入框为前台，便于点表情精准投回）。
    /// 幂等：窗口已可见时直接返回，不重复 SW_SHOW / 不重复恢复交互。
    /// flag 与 win32 调用只在本方法内发生，入口回调不得自行 set flag。
    /// </summary>
    public void ShowWindow(bool activate)
    {
        // 已可见（且非最小化）则跳过，避免重复显示导致的状态/清理错位
        if (NativeMethods.IsWindowVisible(_hWnd) && !NativeMethods.IsIconic(_hWnd))
        {
            Log("[窗口] 显示：已可见，跳过");
            _isVisible = true;
            return;
        }

        // 最小化窗口必须用 SW_RESTORE（SW_SHOW 对 iconic 窗口无效）
        if (NativeMethods.IsIconic(_hWnd))
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_RESTORE);
        else if (activate)
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_SHOW);
        else
            NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_SHOWNOACTIVATE);

        if (activate)
            NativeMethods.SetForegroundWindow(_hWnd);
        // 显示后重新断言置顶，避免最小化/恢复或长期后台后 Z 序被插队
        if (_topMost)
            ApplyTopMost(true);

        _isVisible = true;
        CurrentMainPage?.SetMemeViewVisible(true);
        _fgTimer?.Start();
        ResumeWindowInteractions();

        // 从托盘/快捷键呼出后，将焦点重新定位到当前模式的默认交互控件，
        // 避免焦点残留在系统标题栏关闭按钮上（用户点 X 隐藏后焦点被系统三键截持）。
        // 放在 ShowWindow 统一处理，覆盖所有“显示窗口”入口（托盘显示、切换模式、设置等），
        // 不遗漏。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentMode == AppMode.Full)
                CurrentMainPage?.FocusSearchBox();
            else
                CurrentMiniPage?.FocusDropHint();
        });

        Log($"[窗口] 显示完成 (activate={activate})");
    }

    /// <summary>
    /// 统一“隐藏主窗口”入口：所有隐藏窗口的路径都必须走这里，
    /// 以保证拖拽/轮询等回调在隐藏期间被停用，避免触发 native AV。
    /// 幂等：窗口已隐藏时直接返回，不重复 SW_HIDE / 不重复清理。
    /// flag 与 win32 调用只在本方法内发生，入口回调不得自行 set flag。
    /// </summary>
    private void HideWindow()
    {
        // 已隐藏（不可见且非最小化）则跳过，避免重复隐藏导致的清理错位
        if (!NativeMethods.IsWindowVisible(_hWnd) && !NativeMethods.IsIconic(_hWnd))
        {
            Log("[窗口] 隐藏：已隐藏，跳过");
            _isVisible = false;
            return;
        }

        NativeMethods.ShowWindow(_hWnd, NativeMethods.SW_HIDE);
        _isVisible = false;
        // 窗口隐藏：两种模式都断开图像引用并统一 GC（Mini 模式此前没有此释放路径）。
        ReleaseCurrentPageImages();
        SuspendWindowInteractions(closing: false);
        Log("[窗口] 隐藏完成 (SW_HIDE)");
    }

    /// <summary>
    /// 切换窗口可见性（供全局快捷键等“呼出/关闭二合一”入口使用）。
    /// 只依据 Win32 真实状态决策，再委托 ShowWindow/HideWindow 执行；
    /// 自身不写 _isVisible，避免与别的入口状态错位。
    /// </summary>
    private void ToggleWindow()
    {
        bool iconic = NativeMethods.IsIconic(_hWnd);
        bool visible = NativeMethods.IsWindowVisible(_hWnd) && !iconic;
        Log($"[窗口] 切换：当前可见={visible} (IsWindowVisible={NativeMethods.IsWindowVisible(_hWnd)}, Iconic={iconic})");
        if (visible)
            HideWindow();
        else
            ShowWindow(activate: false);
    }

    /// <summary>托盘“退出”：允许真正关闭窗口并退出程序</summary>
    public void RequestExit()
    {
        SaveWindowSize();
        DeleteInstanceLock();
        _allowClose = true;
        _isClosing = true;
        this.Close();
    }

    /// <summary>
    /// 开机自启(--hidden)使用：窗口创建后直接隐藏到后台、只留托盘，
    /// 不抢焦点、不激活，避免启动瞬间闪一下界面。
    /// </summary>
    public void StartHidden()
    {
        HideWindow();
    }



    /// <summary>
    /// 普通启动使用：显示窗口但不抢前台焦点（SW_SHOWNOACTIVATE）。
    /// 这样用户正在用的外部应用(QQ 等)仍是前台，_fgTimer 在窗口“可见未激活”
    /// 期间持续记录其窗口句柄，点表情时才能把 Ctrl+V 精准投回输入框。
    /// 若直接 Activate() 抢前台，则 Tapped 时前台已是本窗口，无法拿到外部窗口。
    /// </summary>
    public void ShowWithoutActivate()
    {
        ShowWindow(activate: false);
    }

    /// <summary>
    /// 手动将图标设到窗口，使独立发布（非 MSIX）时任务栏/标题栏也显示 Logo。
    /// WinUI 3 不会自动从 EXE 图标继承窗口图标，需通过 WM_SETICON 显式设置。
    /// </summary>
    private void SetTaskbarIcon()
    {
        try
        {
            var hIcon = LoadAppIcon();
            if (hIcon == IntPtr.Zero)
                return;

            // 同时设置大/小两套，任务栏用 small，标题栏/alt-tab 用 big
            NativeMethods.SendMessage(_hWnd, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_SMALL, hIcon);
            NativeMethods.SendMessage(_hWnd, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_BIG, hIcon);
        }
        catch (Exception ex)
        {
            Logger.Log($"[MemeManager] 设置窗口图标失败: {ex}");
        }
    }

    // 应用显示名（标题条、对话框等共用）。
    public const string AppName = "MemeManager";

    // 配置/锁文件所在目录与文件名（%LOCALAPPDATA%\AppName 下），供各模块复用，避免散落字面量。
    public const string ConfigFileName = "config.json";
    public const string InstanceLockFileName = "instance.lock";
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    public static string ConfigPath => Path.Combine(AppDataDir, ConfigFileName);
    public static string InstanceLockPath => Path.Combine(AppDataDir, InstanceLockFileName);

    // 窗口标题文本：AppName + 程序集 InformationalVersion（CI 传 -p:AppVersion=vX.Y.Z，否则本地时间戳 dev build）。
    // dev build 形如 "... dev build+<hash>"，把 "+" 及其后的 hash 去掉，只保留可读部分。
    public static string WindowTitle
    {
        get
        {
            var ver = typeof(MainWindow).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (ver != null)
            {
                int plus = ver.IndexOf('+');
                if (plus >= 0) ver = ver.Substring(0, plus);
                return $"{AppName} {ver}";
            }
            return AppName;
        }
    }

    // AppIcon.ico 路径（发布/调试均会拷贝到 Assets 下），托盘图标与标题条 Logo 共用。
    public static string AppIconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    private IntPtr LoadAppIcon()
    {
        // 从 exe 运行目录的 AppIcon.ico 文件加载（LoadImage 已验证可用）
        var path = AppIconPath;
        if (File.Exists(path))
        {
            var h = NativeMethods.LoadImage(
                IntPtr.Zero, path, NativeMethods.IMAGE_ICON, 0, 0,
                NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
            if (h != IntPtr.Zero)
                return h;
        }

        Logger.Log("[MemeManager] 未找到 AppIcon.ico");
        return IntPtr.Zero;
    }

    // ---------- Win32 层拖入文件（WM_DROPFILES）----------

    private void HandleDropFiles(IntPtr hDrop)
    {
        // 注意：此函数在窗口过程(WM_DROPFILES)回调内同步执行，
        // 绝对不能在里面阻塞等待异步(会卡死消息泵)。只同步收集路径，导入交给页面异步处理。
        try
        {
            uint count = NativeMethods.DragQueryFile(hDrop, 0xFFFFFFFFu, IntPtr.Zero, 0);
            var paths = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                uint len = NativeMethods.DragQueryFile(hDrop, i, IntPtr.Zero, 0);
                if (len == 0) continue;
                IntPtr buf = Marshal.AllocCoTaskMem((int)(len + 1) * 2);
                try
                {
                    NativeMethods.DragQueryFile(hDrop, i, buf, len + 1);
                    string path = Marshal.PtrToStringUni(buf) ?? string.Empty;
                    if (File.Exists(path) && MainPage.IsImage(Path.GetExtension(path)))
                        paths.Add(path);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buf);
                }
            }
            NativeMethods.DragFinish(hDrop);

            if (paths.Count > 0)
                (RootFrame.Content as IExternalDropPage)?.HandleExternalDropPaths(paths);
        }
        catch (Exception ex)
        {
            Log("WM_DROPFILES 处理失败: " + ex.Message);
        }
    }

    // ---------- 热键 / 窗口过程 ----------

    // 跨进程“呼出已有实例”消息 ID（由字符串注册，系统保证全局唯一）
    private static readonly uint _showExistingMsg = NativeMethods.RegisterWindowMessageW("MemeManager_ShowExisting");

    private IntPtr NewWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
    {
        // 重复启动的新实例请求呼出：抛回 UI 线程激活自身（绕过前台锁定）
        if (uMsg == _showExistingMsg)
        {
            DispatcherQueue.TryEnqueue(() => ShowAndActivate());
            return IntPtr.Zero;
        }

        if (uMsg == NativeMethods.WM_MOUSEACTIVATE)
        {
            // 允许点击窗口时正常激活（这样文本框可以输入），
            // 不再返回 MA_NOACTIVATE 以免整个窗口无法获得焦点。
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        if (uMsg == NativeMethods.WM_ACTIVATE)
        {
            // 记录“另一个窗口”的句柄：无论是我们被激活（lParam=被挤掉的窗口）
            // 还是我们失去激活（lParam=新激活的窗口），都能拿到上一次的外部应用，
            // 粘贴时把 Ctrl+V 投回给它。
            int state = (int)wParam & 0xFFFF;
            _isActive = state != NativeMethods.WA_INACTIVE;
            if (lParam != IntPtr.Zero)
            {
                _prevActiveHwnd = lParam;
            }
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        if (uMsg == NativeMethods.WM_SYSCOMMAND)
        {
            // 最大化 / 最小化 / 取消最大化 等系统命令统一交给默认过程处理，
            // 不再拦截（此前拦截 SC_RESTORE 会导致最大化后第二次点击无反应）。
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        if (uMsg == NativeMethods.WM_HOTKEY && (int)wParam == HOTKEY_ID)
        {
            ToggleWindow();
            return IntPtr.Zero;
        }

        if (uMsg == NativeMethods.WM_CLOSE)
        {
            // 普通点右上角 X：只隐藏窗口，后台（托盘）继续运行
            if (!_allowClose)
            {
                Log("WM_CLOSE: 仅隐藏窗口（后台保留）");
                HideWindow();
                return IntPtr.Zero;
            }
            // _allowClose=true（托盘退出）时放行，交给默认处理真正关闭
            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        if (uMsg == NativeMethods.WM_DROPFILES)
        {
            HandleDropFiles(wParam);
            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    // 切换/断言窗口置顶（参考 PowerToys Always On Top）：
    // 仅用 SetWindowPos 调整 Z 序，不携带 SWP_SHOWWINDOW，与显示/激活逻辑解耦。
    private void ApplyTopMost(bool topMost)
    {
        NativeMethods.SetWindowPos(
            _hWnd,
            topMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
    }

    // 最小化结束事件回调：仅针对本窗口且配置为置顶时，重新 SetWindowPos 置顶一次
    private void WinEventCallback(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == _hWnd && idObject == 0 && _topMost && !_isClosing)
        {
            ApplyTopMost(true);
        }
    }

    // 窗口即将隐藏/销毁时调用：停掉页面层拖拽能力、停止前台窗口轮询定时器，
    // 防止拖拽会话进行中或隐藏期间这些回调触发 XAML 操作导致 native AV(0xc0000005)。
    // closing=true 表示真正销毁窗口，会置 _isClosing 阻止一切后续异步 XAML 操作。
    private void SuspendWindowInteractions(bool closing)
    {
        if (closing) _isClosing = true;
        Log($"[防护] SuspendWindowInteractions: closing={closing}, _isVisible={_isVisible}");

        CurrentMainPage?.SuspendInteractions();

        // 停前台窗口轮询定时器
        _fgTimer?.Stop();
    }

    // 窗口重新显示时调用：恢复页面层拖拽能力
    private void ResumeWindowInteractions()
    {
        if (_isClosing) return;
        CurrentMainPage?.ResumeInteractions();
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        SuspendWindowInteractions(closing: true);
        App.DataEngine.Watcher?.Stop();
        NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_ID);
        // 注销最小化结束事件钩子，避免泄漏
        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
    }

    // ---------- 全局快捷键 ----------

    private void RegisterConfiguredHotKey()
    {
        NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_ID);
        var cfg = App.DataEngine.Config;
        NativeMethods.RegisterHotKey(_hWnd, HOTKEY_ID, cfg.HotKeyModifiers, cfg.HotKeyVk);
    }

    /// <summary>
    /// 设置页修改快捷键后调用，重新注册并持久化
    /// </summary>
    public void ApplyHotKeyConfig(uint modifiers, ushort vk)
    {
        App.DataEngine.Config.HotKeyModifiers = modifiers;
        App.DataEngine.Config.HotKeyVk = vk;
        RegisterConfiguredHotKey();
        _ = App.DataEngine.SaveConfigAsync();
    }

    /// <summary>
    /// 当前配置的快捷键文本，如 "Ctrl+Alt+." / "Ctrl+B" / "Ctrl+F8"
    /// </summary>
    public static string HotKeyText(uint modifiers, ushort vk)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & 0x8) != 0) parts.Add("Win");
        if ((modifiers & 0x1) != 0) parts.Add("Alt");
        if ((modifiers & 0x2) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x4) != 0) parts.Add("Shift");

        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(ushort vk)
    {
        // 常见按键手动映射（GetKeyNameText 依赖扫描码，部分组合会返回空）
        switch (vk)
        {
            case 0x41: return "A"; case 0x42: return "B"; case 0x43: return "C";
            case 0x44: return "D"; case 0x45: return "E"; case 0x46: return "F";
            case 0x47: return "G"; case 0x48: return "H"; case 0x49: return "I";
            case 0x4A: return "J"; case 0x4B: return "K"; case 0x4C: return "L";
            case 0x4D: return "M"; case 0x4E: return "N"; case 0x4F: return "O";
            case 0x50: return "P"; case 0x51: return "Q"; case 0x52: return "R";
            case 0x53: return "S"; case 0x54: return "T"; case 0x55: return "U";
            case 0x56: return "V"; case 0x57: return "W"; case 0x58: return "X";
            case 0x59: return "Y"; case 0x5A: return "Z";
            case 0x30: return "0"; case 0x31: return "1"; case 0x32: return "2";
            case 0x33: return "3"; case 0x34: return "4"; case 0x35: return "5";
            case 0x36: return "6"; case 0x37: return "7"; case 0x38: return "8";
            case 0x39: return "9";
            case >= 0x70 and <= 0x87: return "F" + (vk - 0x6F); // F1..F24
        }

        // 其余按键用 GetKeyNameText（正确构造 lParam：扫描码 + 扩展键位）
        uint scan = NativeMethods.MapVirtualKey(vk, 0); // MAPVK_VK_TO_VSC
        // 扩展键（方向键、小键盘、右 Ctrl/Alt 等）需要第 24 位
        bool extended = vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 // 翻页/方向
            or 0x2D or 0x2E or 0x2F or 0x6A or 0x6B or 0x6C or 0x6D or 0xA3 or 0xA4 or 0xA5; // Ins/Del/Home/End + 右修饰键
        int lParam = ((int)scan << 16) | (extended ? 0x01000000 : 0);

        var sb = new System.Text.StringBuilder(64);
        if (NativeMethods.GetKeyNameTextW(lParam, sb, sb.Capacity) > 0 && sb.Length > 0)
        {
            // 去掉可能存在的 “(数字键盘)” 等冗余描述，保留简洁名
            return sb.ToString().Replace(" (数字键盘)", "").Replace(" (小键盘)", "").Trim();
        }

        // OEM / 标点等：用 OEM 映射表自行推断
        return vk switch
        {
            0xBE => ".", 0xBC => ",", 0xBB => "=", 0xBD => "-",
            0xBA => ";", 0xDE => "'", 0xC0 => "`", 0xDB => "[",
            0xDD => "]", 0xDC => "\\", 0xE2 => "\\",
            _ => "0x" + vk.ToString("X2")
        };
    }

    // ---------- 标题栏主题（简单自定义） ----------

    // 让系统默认标题栏跟随 App 配置的主题：
    //  - Light/Dark 显式指定标题栏明暗，使主动切主题时标题栏同步变化；
    //  - System 则使用 UseDefaultAppMode，由系统按当前明暗渲染。
    public void ApplyTitleBarTheme()
    {
        if (_appWindow == null) return;
        try
        {
            var theme = App.DataEngine.Config.Theme;
            _appWindow.TitleBar.PreferredTheme = theme switch
            {
                ThemeMode.Dark => Microsoft.UI.Windowing.TitleBarTheme.Dark,
                ThemeMode.Light => Microsoft.UI.Windowing.TitleBarTheme.Light,
                _ => Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"[标题栏] 设置 PreferredTheme 失败: {ex.Message}");
        }
    }
}
