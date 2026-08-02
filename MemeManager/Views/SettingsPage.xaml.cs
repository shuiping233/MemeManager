using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media.Imaging;
using MemeManager.Infrastructure;
using MemeManager.Models;
using MemeManager.ViewModels;
using MemeManager.Services;

namespace MemeManager.Views;

public sealed partial class SettingsPage : Page
{
    private readonly MemeDataEngine _engine =
        App.GetService<MemeDataEngine>();

    // 语言下拉项（由 Strings 目录自动发现，显示名取自 resw）。
    public IList<LangHelper.LanguageOption> LanguageItems { get; private set; } = [];

    private readonly ConfigService ConfigService = App.GetService<ConfigService>();

    public SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

    // 单例 VM 的事件订阅需在页面卸载时反订阅，否则每次打开设置浮窗都会新增一份处理器而累积
    // （SettingsViewModel 是 AddSingleton，页却是每次重新实例化的）。
    private readonly Action _onBrowseFolder;
    private readonly Action<string> _onOpenFolder;
    private readonly Action _onClose;
    private readonly Action _onAbout;

    public SettingsPage()
    {
        // 语言下拉项需在 InitializeComponent 之前就绪，x:Bind(OneTime) 才能正确绑定。
        LanguageItems = LangHelper.BuildLanguageOptions();

        InitializeComponent();

        DataContext = App.GetService<SettingsViewModel>();
        _onBrowseFolder = () => _ = BrowseFolderAsync();
        _onOpenFolder = path => _ = OpenFolderAsync(path);
        _onClose = () => _ = SaveAndCloseAsync();
        _onAbout = () => _ = ShowAboutAsync();
        ViewModel.BrowseFolderRequested += _onBrowseFolder;
        ViewModel.OpenFolderRequested += _onOpenFolder;
        ViewModel.CloseRequested += _onClose;
        ViewModel.AboutRequested += _onAbout;

        Unloaded += SettingsPage_Unloaded;

        LocalizeStaticStrings();

        var cfg = ConfigService.Config;
        ThemeComboBox.SelectedIndex = (int)cfg.Theme;
        StoragePathBox.Text = cfg.StoragePath;
        HotKeyBox.Text = HotKeyUtils.ToText(cfg.HotKeyModifiers, cfg.HotKeyVk);
        SaveLogToggle.IsOn = cfg.SaveLogFile;
        EcoModeToggle.IsOn = cfg.EcoMode;
        AutoStartToggle.IsOn = StartupManager.IsEnabled();
        _initialAutoStart = AutoStartToggle.IsOn;
        AllowMiniModeToggle.IsOn = cfg.AllowMiniMode;
        UseControlReuseToggle.IsOn = cfg.UseControlReuse;
        ExplorerStyleMultiSelectToggle.IsOn = cfg.ExplorerStyleMultiSelect;
        StorageFileDragToggle.IsOn = cfg.StorageFileDrag;

        // 预览图设置：缺失时用默认 800x600 / 400ms
        PreviewMaxWidthBox.Text = (cfg.PreviewMaxWidth > 0 ? cfg.PreviewMaxWidth : 800).ToString();
        PreviewMaxHeightBox.Text = (cfg.PreviewMaxHeight > 0 ? cfg.PreviewMaxHeight : 600).ToString();
        PreviewDelayBox.Text = (cfg.PreviewDelayMs > 0 ? cfg.PreviewDelayMs : 400).ToString();

        // 进入设置时记录已有的有效路径，作为手动输入校验失败时的回退基准
        _originalStoragePath = cfg.StoragePath;

        // 语言：先填充下拉项，再按 config 设置初始选中项（null=跟随系统）
        LanguageItems = LangHelper.BuildLanguageOptions();
        LanguageComboBox.ItemsSource = LanguageItems;
        LanguageComboBox.SelectedIndex = LangHelper.IndexFromLangCode(cfg.Language, LanguageItems);
        UpdateLanguageStatus();

        this.KeyDown += SettingsPage_KeyDown;

        // 构造期间对下拉框赋值会触发 SelectionChanged，用此标志跳过初始化的多余写盘。
        _loaded = true;
    }

    private bool _loaded;

    // 进入设置时已有的有效路径（校验失败回退用，而非默认路径）
    private string? _originalStoragePath;

    // 进入设置时开机自启的初始状态，用于判断用户是否真正改动过该开关。
    private bool _initialAutoStart;

    // 填充默认快捷键提示等无法用 Uid 直接绑定的静态文本（ComboBox 选项已通过 XAML Uid 本地化）。
    private void LocalizeStaticStrings()
    {
        HotKeyBox.Text = Localization.Get("Settings_HotKey_Default");
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 即选即预览：立刻切换主题，无需点“完成”
        var theme = (ThemeMode)ThemeComboBox.SelectedIndex;
        ConfigService.Config.Theme = theme;
        App.ApplyTheme();
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
        // 反订阅单例 VM 的事件，避免处理器累积（每次打开设置浮窗会新建本页实例）。
        if (DataContext is SettingsViewModel vm)
        {
            vm.BrowseFolderRequested -= _onBrowseFolder;
            vm.OpenFolderRequested -= _onOpenFolder;
            vm.CloseRequested -= _onClose;
            vm.AboutRequested -= _onAbout;
        }
        Unloaded -= SettingsPage_Unloaded;
    }

    // 关于弹窗：展示 Logo、应用简介、开源许可、项目源码超链接与作者（经典 Windows 风格关于框）。
    private async Task ShowAboutAsync()
    {
        if (XamlRoot == null) return;

        var logo = new Image
        {
            Width = 48,
            Height = 48,
            Margin = new Thickness(0, 0, 0, 12),
            Source = new BitmapImage(new Uri(AppConstants.IconPath)),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var desc = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Localization.Get("About_Description"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var license = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Localization.Get("About_License"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var author = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = string.Format(Localization.Get("About_Author"), "shuiping233"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var star = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Localization.Get("About_StarHint"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        // 左对齐的链接按钮（文本居左、整体靠左），避免默认居中显得松散。
        HyperlinkButton MakeLink(string textKey, string uri)
            => new HyperlinkButton
            {
                Content = Localization.Get(textKey),
                NavigateUri = new Uri(uri),
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 4),
            };

        var link = MakeLink("About_SourceLink", "https://github.com/shuiping233/MemeManager");
        var depUi = MakeLink("About_Dep_MicrosoftUi", "https://github.com/microsoft/microsoft-ui-xaml");
        var depSdk = MakeLink("About_Dep_AppSdk", "https://learn.microsoft.com/windows/apps/windows-app-sdk/");
        var depLoc = MakeLink("About_Dep_Localizer", "https://github.com/AndrewKeepCoding/WinUI3Localizer");

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(logo);
        panel.Children.Add(desc);
        panel.Children.Add(license);
        panel.Children.Add(author);
        panel.Children.Add(star);
        panel.Children.Add(link);
        panel.Children.Add(depUi);
        panel.Children.Add(depSdk);
        panel.Children.Add(depLoc);

        var dialog = new ContentDialog
        {
            Title = Localization.Get("Settings_About"),
            Content = panel,
            CloseButtonText = Localization.Get("Dialog_OK"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = DialogHelper.DialogTheme,
        };

        await DialogHelper.SafeShowAsync(dialog);
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
        try
        {
            // 确保目录存在
            System.IO.Directory.CreateDirectory(path);
            Logger.Log($"[Settings] 打开 {path} 文件夹");
            await Windows.System.Launcher.LaunchFolderPathAsync(path);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Settings] 打开 {path} 文件夹错误: {ex.Message}");
        }
    }

    // 用户手动修改路径文本框时校验：目录存在则记录，不存在则提示并回退到进入设置前的有效路径
    private bool _revertingPath;
    private async void StoragePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_revertingPath) return;

        var text = StoragePathBox.Text?.Trim() ?? string.Empty;
        // 空字符串暂不打扰（用户可能正在输入中）
        if (string.IsNullOrWhiteSpace(text)) return;

        if (Directory.Exists(text))
        {
            // 有效路径：仅记录，真正保存延后到点击“完成”
            return;
        }

        // 目录不存在：弹窗提示并回退到进入设置前保存的有效路径
        await DialogHelper.ShowPathNotFoundAsync(XamlRoot, text);

        var fallback = _originalStoragePath ?? Utils.DefaultDataStoragePath();
        _revertingPath = true;
        StoragePathBox.Text = fallback;
        _revertingPath = false;
    }

    public async Task SaveAsync()
    {
        // 防止重复保存：点击“完成”已保存一次，浮窗 Closed 事件又会触发一次
        if (_saved) return;
        _saved = true;

        var theme = (ThemeMode)ThemeComboBox.SelectedIndex;

        double.TryParse(PreviewMaxWidthBox.Text, out double pw);
        double.TryParse(PreviewMaxHeightBox.Text, out double ph);
        int.TryParse(PreviewDelayBox.Text, out int delay);

        string? newStoragePath = null;
        bool pathChanged = false;
        var typedPath = StoragePathBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(typedPath) && Directory.Exists(typedPath))
        {
            newStoragePath = typedPath;
            pathChanged = true;
        }

        var prev = ConfigService.Config;
        double prevW = prev.PreviewMaxWidth, prevH = prev.PreviewMaxHeight;
        int prevDelay = prev.PreviewDelayMs;

        await _engine.UpdateConfigAsync(cfg =>
        {
            cfg.Theme = theme;
            cfg.SaveLogFile = SaveLogToggle.IsOn;
            cfg.EcoMode = EcoModeToggle.IsOn;
            cfg.AutoStart = AutoStartToggle.IsOn;
            cfg.UseControlReuse = UseControlReuseToggle.IsOn;
            cfg.ExplorerStyleMultiSelect = ExplorerStyleMultiSelectToggle.IsOn;
            cfg.StorageFileDrag = StorageFileDragToggle.IsOn;
            cfg.AllowMiniMode = AllowMiniModeToggle.IsOn;
            if (pw > 0) cfg.PreviewMaxWidth = pw;
            if (ph > 0) cfg.PreviewMaxHeight = ph;
            if (delay > 0) cfg.PreviewDelayMs = delay;
            if (pathChanged) cfg.StoragePath = newStoragePath!;
        });

        if (pw > 0 && ph > 0 && (pw != prevW || ph != prevH))
            Logger.Log($"[Settings] 预览图最大分辨率: {prevW}x{prevH} -> {pw}x{ph}");
        if (delay > 0 && delay != prevDelay)
            Logger.Log($"[Settings] 预览图触发延时: {prevDelay}ms -> {delay}ms");

        App.ApplyTheme();

        // 预览分辨率 / 存放路径变化：重建主窗口数据以生效
        if ((pw > 0 && ph > 0) || pathChanged)
            App.MainWindow.ReloadData();

        if (delay > 0)
            App.MainWindow.ApplyPreviewDelayFromConfig();

        // 复用策略切换：应用新策略并刷新当前列表使其立即生效
        App.MainWindow.ApplyListStrategyFromConfig();
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

    // 请求关闭（由宿主 Flyout 监听后 Hide）
    public event EventHandler? RequestClose;

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
