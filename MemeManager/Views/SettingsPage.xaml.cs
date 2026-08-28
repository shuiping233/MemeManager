using CommunityToolkit.Mvvm.Messaging;
using MemeManager.Infrastructure;
using MemeManager.Models;
using MemeManager.Services;
using MemeManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemeManager.Views;

public sealed partial class SettingsPage : Page
{
    private readonly MemeDataEngine _engine =
        App.GetService<MemeDataEngine>();

    // 语言下拉项（由 Strings 目录自动发现，显示名取自 resw）。
    public IList<LangHelper.LanguageOption> LanguageItems { get; private set; } = [];

    private readonly ConfigService ConfigService = App.GetService<ConfigService>();

    private bool isProgramExiting = false;

    public SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

    // 回调处理器：单例 Page 构造时用 '=' 赋给 SettingsViewModel 的委托属性（不累积、无需反订阅）。
    private readonly Action _onBrowseFolder;
    private readonly Action<string> _onOpenFolder;
    private readonly Action _onClose;
    private readonly Action _onAbout;
    private readonly Action _onProgramExit;

    public SettingsPage()
    {
        // 语言下拉项需在 InitializeComponent 之前就绪，x:Bind(OneTime) 才能正确绑定。
        LanguageItems = LangHelper.BuildLanguageOptions();

        InitializeComponent();

        DataContext = App.GetService<SettingsViewModel>();
        _onBrowseFolder = () => _ = BrowseFolderAsync();
        _onOpenFolder = path => _ = OpenFolderAsync(path);
        _onClose = () => _ = SaveAndCloseAsync();
        _onAbout = () => _ = AboutPage.ShowAsync(XamlRoot);
        _onProgramExit = async () =>
        {
            var result = await DialogHelper.ShowProgramExitNoticeAsync(XamlRoot);
            if (result != ContentDialogResult.Primary) return;
            isProgramExiting = true;
            WeakReferenceMessenger.Default.Send(new CloseAppMessage());
        };
        // 单例 Page 常驻：用 '=' 覆盖回调查至 SettingsViewModel 的委托属性（构造一次即可）。
        ViewModel.BrowseFolderRequested = _onBrowseFolder;
        ViewModel.OpenFolderRequested = _onOpenFolder;
        ViewModel.CloseRequested = _onClose;
        ViewModel.AboutRequested = _onAbout;
        ViewModel.ProgramExitRequested = _onProgramExit;

        LanguageComboBox.ItemsSource = LanguageItems;

        Unloaded += SettingsPage_Unloaded;

        this.KeyDown += SettingsPage_KeyDown;

        ValidatePathDebouncer = new AsyncDebouncer(
            AppConstants.StoragePathValidationDebounce, async (token) => { await ValidatePathAfterDelayAsync(token); return; }
        );
    }

    // 每次设置浮窗打开前调用：从 Config 重新填充全部控件值，并重置"每次进入才有效"的状态。
    // 单例 Page 复用时必须在此恢复初始状态，否则第二次打开会读到上次的值/无法再次保存。
    public void OnShow()
    {
        _loaded = false;

        // 默认快捷键提示（会被下面实际热键覆盖，与原始构造顺序一致以保持语义）。
        LocalizeStaticStrings();

        var cfg = ConfigService.Config;
        ThemeSegmented.SelectedIndex = (int)cfg.Theme;
        ImageStretchSegmented.SelectedIndex = (int)ImageStretchModeHelper.Parse(cfg.ImageStretch);
        StoragePathBox.Text = cfg.StoragePath;
        HotKeyBox.Text = HotKeyUtils.ToText(cfg.HotKeyModifiers, cfg.HotKeyVk);
        EnableHotKeyToggle.IsOn = cfg.EnableHotKey;
        ApplyHotKeyControlsEnabled();
        SaveLogToggle.IsOn = cfg.SaveLogFile;
        EcoModeToggle.IsOn = cfg.EcoMode;
        AutoStartToggle.IsOn = StartupManager.IsEnabled();
        _initialAutoStart = AutoStartToggle.IsOn;
        AutoCheckUpdateToggle.IsOn = cfg.AutoCheckForUpdates;
        AllowMiniModeToggle.IsOn = cfg.AllowMiniMode;
        UseControlReuseToggle.IsOn = cfg.UseControlReuse;
        _initialUseControlReuse = UseControlReuseToggle.IsOn;
        ExplorerStyleMultiSelectToggle.IsOn = cfg.ExplorerStyleMultiSelect;
        StorageFileDragToggle.IsOn = cfg.StorageFileDrag;

        PreviewMaxWidthBox.Text = (cfg.PreviewMaxWidth > 0 ? cfg.PreviewMaxWidth : 800).ToString();
        PreviewMaxHeightBox.Text = (cfg.PreviewMaxHeight > 0 ? cfg.PreviewMaxHeight : 600).ToString();
        PreviewDelayBox.Text = (cfg.PreviewDelayMs > 0 ? cfg.PreviewDelayMs : 400).ToString();

        _originalStoragePath = cfg.StoragePath;
        _saved = false;

        // 取消防抖校验，避免上次遗留的校验弹窗在再次打开时干扰。
        ValidatePathDebouncer.CancelPending();

        LanguageComboBox.SelectedIndex = LangHelper.IndexFromLangCode(cfg.Language, LanguageItems);
        UpdateLanguageStatus();

        _loaded = true;
    }

    private bool _loaded;

    // 进入设置时已有的有效路径（校验失败回退用，而非默认路径）
    private string? _originalStoragePath;

    // 进入设置时开机自启的初始状态，用于判断用户是否真正改动过该开关。
    private bool _initialAutoStart;

    // 进入设置时控件复用策略的初始状态，用于判断用户是否真正切换过 reuse/rebuild。
    private bool _initialUseControlReuse;

    // 填充默认快捷键提示等无法用 Uid 直接绑定的静态文本（ComboBox 选项已通过 XAML Uid 本地化）。
    private void LocalizeStaticStrings()
    {
        HotKeyBox.Text = Localization.Get("Settings_HotKey_Default");
    }

    private void ThemeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 即选即预览：立刻切换主题，无需点“完成”
        var theme = (ThemeMode)ThemeSegmented.SelectedIndex;
        ConfigService.Config.Theme = theme;
        App.ApplyTheme();
    }

    private void ImageStretchSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 即选即生效：更新配置并通知主列表所有缩略图刷新拉伸方式（无需重建列表）
        if (ImageStretchSegmented.SelectedIndex < 0) return;
        var mode = (ImageStretchMode)ImageStretchSegmented.SelectedIndex;
        ConfigService.Config.ImageStretch = mode.ToString();
        App.GetService<MainViewModel>().ApplyImageStretchToAll();
    }

    private void UpdateLanguageStatus()
    {
        // 实时展示：切换后 Localization.Get 应随当前语言返回对应文本（证明不重启即生效）
        LanguageStatusText.Text = Localization.Get("Language_Status_Switched");
    }

    private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (LanguageComboBox.SelectedIndex < 0) return;
        var code = LangHelper.LangCodeFromIndex(LanguageComboBox.SelectedIndex, LanguageItems);

        // 真正切换语言（库支持运行时切换，无需重启）；统一走 LangHelper（已写入配置）。
        LangHelper.SetLanguage(code);
        ConfigService.Config.Language = code;

        // 切换语言后，下拉项自身的显示名需重新取 resw 文案刷新（原地更新，避免重设
        // ItemsSource 触发 ComboBox 重新选择并递归触发 SelectionChanged）。
        LangHelper.RefreshLanguageOptions(LanguageItems);

        await ConfigService.SaveConfigAsync();

        UpdateLanguageStatus();

        // 动态文案（更新检查状态）依赖语言，切换后重算
        ViewModel.RefreshLocalizedTexts();
    }

    private void SettingsPage_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_recording)
        {
            e.Handled = true;

            // 只记录修饰键阶段，等用户按下真正的键
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;
            bool ctrl = IsDown(Windows.System.VirtualKey.Control) || IsDown(Windows.System.VirtualKey.LeftControl) || IsDown(Windows.System.VirtualKey.RightControl);
            bool shift = IsDown(Windows.System.VirtualKey.Shift) || IsDown(Windows.System.VirtualKey.LeftShift) || IsDown(Windows.System.VirtualKey.RightShift);
            bool alt = IsDown(Windows.System.VirtualKey.Menu) || IsDown(Windows.System.VirtualKey.LeftMenu) || IsDown(Windows.System.VirtualKey.RightMenu);
            bool win = IsDown(Windows.System.VirtualKey.LeftWindows) || IsDown(Windows.System.VirtualKey.RightWindows);

            // 组合键的“主键”不能是纯修饰键
            bool isModifier = e.Key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Shift
                or Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftControl
                or Windows.System.VirtualKey.RightControl or Windows.System.VirtualKey.LeftShift
                or Windows.System.VirtualKey.RightShift or Windows.System.VirtualKey.LeftMenu
                or Windows.System.VirtualKey.RightMenu or Windows.System.VirtualKey.LeftWindows
                or Windows.System.VirtualKey.RightWindows;

            if (isModifier) return;

            uint mods = 0;
            if (ctrl) mods |= 0x2;
            if (shift) mods |= 0x4;
            if (alt) mods |= 0x1;
            if (win) mods |= 0x8;

            ushort vk = (ushort)e.Key;
            StopRecording();
            App.MainWindow.ApplyHotKeyConfig(mods, vk);
            HotKeyBox.Text = HotKeyUtils.ToText(mods, vk);
            return;
        }
    }

    private static bool IsDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 进入设置页时把焦点放到“完成”按钮上，避免焦点落在首个可聚焦控件
        // （主题 ComboBox）导致回车变成打开下拉菜单而非确认。
        CloseButton.Focus(FocusState.Programmatic);
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // 单例 Page 常驻复用时，不断开 x:Bind（StopTracking 会使 OneWay 绑定停更）、不自解绑 Unloaded。
        // 仅取消防抖校验（浮窗关闭后不再弹窗）。
        ValidatePathDebouncer.CancelPending();
    }

    private void SaveLogToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 改动延后到点击“完成”时保存
    }

    private void EcoModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 即时生效：切换进程级效率模式（保存延后到点击“完成”）
        EcoQos.ApplyProcessLevel(EcoModeToggle.IsOn);
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 改动延后到点击“完成”时保存
    }

    private void AutoCheckUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 改动延后到点击“完成”时保存
    }

    private void UseControlReuseToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 改动延后到点击“完成”时保存
    }

    private void StorageFileDragToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 改动延后到点击“完成”时保存
    }

    private void ExplorerStyleMultiSelectToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 改动延后到点击“完成”时保存
    }

    private void AllowMiniModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // 改动延后到点击“完成”时保存
    }

    private void EnableHotKeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ApplyHotKeyControlsEnabled();

        // 录制过程中被关掉：取消录制，避免页面继续吞按键、按钮文案卡在“取消”
        if (!EnableHotKeyToggle.IsOn && _recording)
            StopRecording();
    }

    // 联动：开关关闭时禁用快捷键文本框与录制按钮
    private void ApplyHotKeyControlsEnabled()
    {
        HotKeyBox.IsEnabled = EnableHotKeyToggle.IsOn;
        RecordHotKeyButton.IsEnabled = EnableHotKeyToggle.IsOn;
    }

    // 仅允许输入非负整数（数字 + 空串），非数字内容直接拒绝
    private void PreviewNumberBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        args.Cancel = !(string.IsNullOrEmpty(args.NewText) || args.NewText.All(char.IsDigit));
    }

    private void PreviewResolution_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 仅做合法性校验，真正保存延后到点击“完成”
        if (!double.TryParse(PreviewMaxWidthBox.Text, out double w) || w <= 0) return;
        if (!double.TryParse(PreviewMaxHeightBox.Text, out double h) || h <= 0) return;
    }

    private void PreviewDelay_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 仅做合法性校验，真正保存延后到点击“完成”
        if (!int.TryParse(PreviewDelayBox.Text, out int ms) || ms <= 0) return;
    }

    private bool _recording;

    private void RecordHotKeyButton_Click(object sender, RoutedEventArgs e)
    {
        _recording = true;
        HotKeyBox.Text = Localization.Get("Settings_HotKey_PressKeys");
        RecordHotKeyButton.Content = Localization.Get("Settings_Cancel");
        RecordHotKeyButton.Click -= RecordHotKeyButton_Click;
        RecordHotKeyButton.Click += CancelRecord_Click;
        this.Focus(FocusState.Programmatic);
    }

    private void CancelRecord_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        var cfg = ConfigService.Config;
        HotKeyBox.Text = HotKeyUtils.ToText(cfg.HotKeyModifiers, cfg.HotKeyVk);
    }

    private void StopRecording()
    {
        _recording = false;
        RecordHotKeyButton.Content = Localization.Get("Settings_Record");
        RecordHotKeyButton.Click -= CancelRecord_Click;
        RecordHotKeyButton.Click += RecordHotKeyButton_Click;
    }

    private async Task BrowseFolderAsync()
    {
        // 打开系统文件选择器期间屏蔽背后图片的悬停预览浮窗，选完再恢复。
        App.MainWindow.IsFilePickerOpen = true;
        try
        {
            var folder = await PickerHelper.PickFolderAsync();
            if (folder != null)
            {
                Logger.Log($"[Settings] BrowseButton_Click: 成功选择文件夹: {folder}");
                StoragePathBox.Text = folder;

                // 立即写入并刷新：Flyout 打开文件选择器会失焦，可能导致设置页实例被换，
                // 若延后到 SaveAsync 会读到旧实例的默认值
                await _engine.UpdateConfigAsync(cfg => cfg.StoragePath = folder);
                App.MainWindow.ReloadData();
                Logger.Log($"[Settings] BrowseButton_Click: 已立即保存存放路径并刷新: {folder}");
            }
            else
            {
                Logger.Log("[Settings] BrowseButton_Click: 用户取消或未选择任何文件夹");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] BrowseButton_Click 异常: {ex}");
            await ShowErrorAsync(Localization.Get("Settings_OpenFolderPickerFailed"), ex.ToString());
        }
        finally
        {
            // 选择器结束：解除预览浮窗屏蔽，背后图片恢复可触发浮窗。
            App.MainWindow.IsFilePickerOpen = false;
        }
    }

    private Task ShowErrorAsync(string title, string detail) =>
        DialogHelper.ShowErrorAsync(this.XamlRoot, title, detail);

    private async Task OpenFolderAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var trimmed = path.Trim();
        // 打开前校验：拒绝相对路径与不存在的路径。
        // 此前直接 CreateDirectory(path) + LaunchFolderPathAsync 会让 "../" 之类相对路径
        // 在当前工作目录下被创建/打开到错误位置。
        var err = ValidateStoragePath(trimmed);
        if (err != StoragePathError.None)
        {
            await ShowStoragePathErrorAsync(err, trimmed);
            return;
        }

        try
        {
            Logger.Log($"[Settings] 打开 {trimmed} 文件夹");
            await Windows.System.Launcher.LaunchFolderPathAsync(trimmed);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] 打开 {trimmed} 文件夹错误: {ex.Message}");
        }
    }

    // 用户手动修改路径文本框时校验：目录存在则记录，不存在则提示并回退到进入设置前的有效路径。
    // 注意：不能只用 Directory.Exists 判断——相对路径（如 "../"）会在当前工作目录下解析，
    // 恒"存在"而漏过（用户填 "../" 打开的是错误位置）。必须叠加 Path.IsPathRooted 拒绝相对路径。
    private bool _revertingPath;

    // 存储路径校验结果：合法 / 相对路径非法 / 目录不存在（空输入视为中性，不打扰输入中状态）
    private enum StoragePathError { None, Relative, NotFound }

    private static StoragePathError ValidateStoragePath(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return StoragePathError.None;
        // 相对路径（"../"、"\folder"、"folder\sub"）一律拒绝
        if (!Path.IsPathRooted(text)) return StoragePathError.Relative;
        // 必须是已存在的文件夹
        if (!Directory.Exists(text)) return StoragePathError.NotFound;
        return StoragePathError.None;
    }

    private async Task ShowStoragePathErrorAsync(StoragePathError err, string path)
    {
        if (err == StoragePathError.Relative)
            await DialogHelper.ShowStoragePathInvalidAsync(XamlRoot, path);
        else
            await DialogHelper.ShowPathNotFoundAsync(XamlRoot, path);
    }

    private readonly AsyncDebouncer ValidatePathDebouncer;

    private void StoragePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_revertingPath) return;
        ValidatePathDebouncer.Trigger();
    }

    private async Task ValidatePathAfterDelayAsync(CancellationToken token)
    {
        // 已被更新的输入或页面卸载取消：本次校验作废
        if (token.IsCancellationRequested) return;

        var text = StoragePathBox.Text?.Trim() ?? string.Empty;
        // 空字符串暂不打扰（用户可能正在输入中）
        if (string.IsNullOrWhiteSpace(text)) return;

        var err = ValidateStoragePath(text);
        if (err == StoragePathError.None)
        {
            // 有效路径：仅记录，真正保存延后到点击"完成"
            return;
        }

        // 弹窗前再次确认：避免与更新的输入竞态，且不叠加在已有模态框上
        // （ContentDialog 同时只能开一个，并发 ShowAsync 会抛 COMException）。
        if (token.IsCancellationRequested || DialogHelper.IsModalOpen) return;

        // 非法：弹窗提示并回退到进入设置前保存的有效路径
        await ShowStoragePathErrorAsync(err, text);

        // 弹窗期间页面可能已卸载（token 取消）：此时不再回退旧控件
        if (token.IsCancellationRequested) return;

        var fallback = _originalStoragePath ?? AppConstants.DefaultMemeDataStoragePath();
        _revertingPath = true;
        StoragePathBox.Text = fallback;
        _revertingPath = false;
    }

    public async Task SaveAsync()
    {
        // 防止重复保存：点击“完成”已保存一次，浮窗 Closed 事件又会触发一次
        if (_saved) return;
        _saved = true;

        var theme = (ThemeMode)ThemeSegmented.SelectedIndex;

        double.TryParse(PreviewMaxWidthBox.Text, out double pw);
        double.TryParse(PreviewMaxHeightBox.Text, out double ph);
        int.TryParse(PreviewDelayBox.Text, out int delay);

        string? newStoragePath = null;
        bool pathChanged = false;
        var typedPath = StoragePathBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(typedPath))
        {
            // 保存入口与输入校验同一规则：拒绝相对路径与不存在路径，
            // 防止把 "../" 之类非法值写入 config（否则下次启动 ResolveBaseDir 会静默回退默认路径）。
            var err = ValidateStoragePath(typedPath);
            if (err == StoragePathError.None)
            {

                newStoragePath = typedPath;
                if (!ConfigService.Config.StoragePath.Equals(newStoragePath))
                    pathChanged = true;
            }
            else
            {
                Logger.Log($"[Settings] 保存配置时存放路径非法，拒绝写入: {typedPath}");
            }
        }

        var prev = ConfigService.Config;
        double prevW = prev.PreviewMaxWidth, prevH = prev.PreviewMaxHeight;
        int prevDelay = prev.PreviewDelayMs;

        await _engine.UpdateConfigAsync(cfg =>
        {
            cfg.Theme = theme;
            cfg.ImageStretch = ((ImageStretchMode)ImageStretchSegmented.SelectedIndex).ToString();
            cfg.SaveLogFile = SaveLogToggle.IsOn;
            cfg.EcoMode = EcoModeToggle.IsOn;
            cfg.AutoStart = AutoStartToggle.IsOn;
            cfg.AutoCheckForUpdates = AutoCheckUpdateToggle.IsOn;
            cfg.UseControlReuse = UseControlReuseToggle.IsOn;
            cfg.ExplorerStyleMultiSelect = ExplorerStyleMultiSelectToggle.IsOn;
            cfg.StorageFileDrag = StorageFileDragToggle.IsOn;
            cfg.AllowMiniMode = AllowMiniModeToggle.IsOn;
            cfg.EnableHotKey = EnableHotKeyToggle.IsOn;
            if (pw > 0) cfg.PreviewMaxWidth = pw;
            if (ph > 0) cfg.PreviewMaxHeight = ph;
            if (delay > 0) cfg.PreviewDelayMs = delay;
            if (pathChanged) cfg.StoragePath = newStoragePath!;
        });

        // 开关状态落盘后立即生效：关则注销全局热键，开则按已保存组合键重新注册
        App.MainWindow.ApplyHotKeyEnabled(EnableHotKeyToggle.IsOn);

        if (pw > 0 && ph > 0 && (pw != prevW || ph != prevH))
            Logger.Log($"[Settings] 预览图最大分辨率: {prevW}x{prevH} -> {pw}x{ph}");
        if (delay > 0 && delay != prevDelay)
            Logger.Log($"[Settings] 预览图触发延时: {prevDelay}ms -> {delay}ms");

        if (!isProgramExiting)
        {
            App.ApplyTheme();
        }

        // 预览分辨率 / 存放路径 / 复用策略变化：重建主窗口数据以生效（其余配置不触发全量刷新）
        bool previewSizeChanged = pw > 0 && ph > 0 && (pw != prevW || ph != prevH);
        bool strategyChanged = UseControlReuseToggle.IsOn != _initialUseControlReuse;
        bool needReload = previewSizeChanged || pathChanged || strategyChanged;

        if (delay > 0)
            App.MainWindow.ApplyPreviewDelayFromConfig();

        // 复用策略 / Mini 可见性：保留无条件调用（仅重建策略对象 + 更新 Mini 按钮可见性，无全量刷新开销），
        // 保证关闭"允许 Mini 模式"后按钮立即消失。
        App.MainWindow.ApplyListStrategyFromConfig();

        // 只有真正需要重建列表/容器的配置变化才全量刷新
        if (needReload)
            App.MainWindow.ReloadData();

        // 仅当用户真正改动过开机自启开关时才写注册表，避免每次打开设置都强制写入。
        if (AutoStartToggle.IsOn != _initialAutoStart)
        {
            bool ok = AutoStartToggle.IsOn ? StartupManager.Enable() : StartupManager.Disable();
            if (!ok)
                Logger.Log("[Settings] 设置开机自启失败（注册表写入被拒绝）");
        }

        Logger.Log("[Settings] 配置已保存");
    }

    // 请求关闭（由宿主 Flyout 监听后 Hide）。用委托属性而非 event：单例 Page 每次打开由 MainPage
    // 用 '=' 覆盖为当前 SettingsFlyout 的句柄，避免 MainPage 重建时累积 handler。
    public EventHandler? RequestClose { get; set; }

    // 是否已保存过（避免“完成”点击与浮窗 Closed 事件重复保存）
    private bool _saved;
    public bool IsSaved => _saved;

    // 保存并关闭设置页（供“完成”按钮与 Flyout 内 Enter 共用）
    public async Task SaveAndCloseAsync()
    {
        await SaveAsync();
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
