# MemeManager 重构路线

## 当前文件架构

```
MemeManager/
├── App.xaml / .cs                       ← 入口；DI 容器 (App.GetService<T>())
├── Views/                               ← XAML + code-behind + UI 辅助
│   ├── MainWindow.xaml / .cs            ← 窗口；标题栏、模式切换、键盘转发、拖入转发
│   ├── Pages/
│   │   ├── MainPage.xaml / .cs          ← 主页面（约 2054 行，仅 UI 接线）
│   │   ├── SettingsPage.xaml / .cs
│   │   └── MiniPage.xaml / .cs
│   ├── Dialogs/DialogHelper.cs          ← 确认/输入/提示弹窗（需 XamlRoot）
│   ├── ViewDragService.cs               ← 原 ImageDragHelper（View 层拖拽适配器，static）
│   ├── ImageBatchOperationRunner.cs     ← 批量操作编排（后台化+进度条+写锁+UI 收尾）
│   ├── BatchProgressHelper.cs           ← 顶部 InfoBar 进度条封装
│   ├── PickerHelper.cs                  ← 文件/文件夹选择器
│   └── IExternalDropPage / IImageReleasablePage  ← Page 对外接口（MainWindow 调用）
├── ViewModels/
│   ├── MainViewModel.cs                 ← 主 VM（ObservableObject + RelayCommand，单例）
│   ├── MemeViewModel.cs / CategoryViewModel.cs  ← Toolkit [ObservableProperty]
│   ├── SettingsViewModel.cs / MiniViewModel.cs
├── Models/                              ← MemeModel / CategoryMetadata / AppConfig / 策略类
├── Services/                            ← 业务编排层（无 UI 依赖）
│   ├── SearchService.cs                 ← 按分类+关键词查询
│   ├── ImportExportService.cs           ← 导入/导出/粘贴导入 + 写锁守卫（IImportExportUi）
│   ├── ClipboardService.cs              ← 复制剪贴板 / 发到外部窗口
│   ├── MemeOperationService.cs          ← 删除/移动/冲突守卫（IMemeOperationUi）
│   └── CategoryService.cs               ← 分类增删改 + 计数计算
└── Infrastructure/                      ← 数据层 + 横切
    ├── MemeDataEngine.cs                ← 数据访问层（Repository/DAO，单例）
    ├── FileWatcher.cs / Logger.cs / Localization.cs / Utils.cs / TrayIcon.cs ...
```

**分层边界**：

- `Infrastructure` = 数据持久化（文件 IO + 缓存 + metadata），不依赖 UI/VM。
- `Services` = 业务编排（后台执行 + 进度条 + 写锁 + 冲突守卫），经 `IImportExportUi` / `IMemeOperationUi` 回调把弹窗甩回 Page，自身 UI 无关。
- `ViewModels` = 视图状态 + 用户意图命令（`RelayCommand`），不引用 `Microsoft.UI.Xaml`。
- `Views` = 纯 UI 接线（事件 handler、Popup、拖拽视觉、选中态镜像），通过事件向 VM 发"请求"。

## 历史遗留 Issue / 需求清单（重构后待处理）

> 来源：GitHub Issues #11 #15 #16 #17 #18 #20 #22 #23 #24（及 shuiping233 反馈）。
> 每项标注：现象 / 根因定位 / 改动文件 / 是否可纯内部搬迁（不用测）。

| # | Issue / 需求 | 现象 | 根因（文件:行） | 处理方向 |
| --- | --- | --- | --- | --- |
| 1 | #24 复选框透明度 & 黑底 | 资源管理器风格多选模式下右上角自绘复选框太淡、黑底不够 | 自绘 `SelectionCheckBox`（MainPage.xaml:324）无背景/描边；WinUI 默认视觉太轻 | 给复选框加半透明深色背景 Panel + 适当 Opacity，提高可见度（纯视觉调优） |
| 2 | #23 复选框消失/残留 | 编辑模式下切换"资源管理器风格"开关→复选框可能消失；退出编辑后残留 | A) `RefreshMemes` 无条件 `SetSelectionBoxVisible(true)`（MainPage.xaml.cs:639）忽略配置；B) 编辑中改设置不重跑 `EnterEditMode` 的 SelectionMode；C) 退出时虚拟化回收容器未重置 `Visibility`，`ContainerContentChanging` 无 else 重置分支（:917-927、:1751-1764） | 统一复选框可见性逻辑：抽一个 `ApplySelectionBoxVisibility()` 由 `EditMode`+`ExplorerStyleMultiSelect` 共决；`ContainerContentChanging` 加 else 重置；退出/设置变更时统一调用 |
| 3 | #22 移除 StorageFile 拖拽警告 | Settings 里"StorageFile 拖拽支持"警告提示语 | SettingsPage.xaml:130-136 的 `Settings_StorageFileDragWarning` 文本块 | 删除该 Warning TextBlock 及其 resw 文案（zh-CN/en-US） |
| 4 | #（数据目录限制）优化写入失败弹窗 + 移除"不可在软件目录"限制 | 选数据目录落在软件目录被拒；写入失败弹窗提示不佳 | `ResolveBaseDir`/`IsInsideAppDir`（MemeDataEngine.cs:44-75）静默回退；SettingsPage.xaml.cs:281-283 弹 `Dialog_StorageInsideAppDir` | 移除 appDir 回退判定 + 对应弹窗与文案；优化 `Dialog_DefaultDirWriteFailed` 提示文案 |
| 5 | #20 减少多余写入 IO | 切分类/切模式/每次配置变更都重复写盘 | `UpdateConfigAsync` 写两遍 config（MemeDataEngine.cs:115-131）；切分类每次写 LastCategory（MainPage.xaml.cs:313/344、MiniPage.xaml.cs:130/135） | 去重：仅在值变化时才写；合并两遍 SaveConfigAsync 为一次；import 单张循环批量写（:370/:399） |
| 6 | #18 设置页新增三个按钮 | 缺"打开日志文件夹""反馈建议""关于" | SettingsPage.xaml/.cs、SettingsViewModel.cs 现有按钮范式（OpenConfigFolder 等） | 仿 `OpenConfigFolderCommand` 加三个 RelayCommand（日志=Launcher 文件夹；反馈=LaunchUri GitHub；关于=DialogHelper.ShowInfoAsync 走事件桥接） |
| 7 | #17 全部表情焦点不恢复 | 退出时停在"全部表情"，重启/全局呼出后焦点跳到第一个普通分类 | A) 构造里 `AllMemesList.ItemsSource` 在 `LoadCategories` 之后才赋值（MainPage.xaml.cs:159-161），选中失效；B) `SetMemeViewVisible` 兜底取 `CategoryList.FirstOrDefault()`（MainWindow.xaml.cs:524→MainPage.xaml.cs:461-466）破坏 AllMemes 状态并改写 LastCategory | 把 AllMemesList.ItemsSource 赋值提前到 LoadCategories 之前；`SetMemeViewVisible` 对 `CategoryKind.All` 走 AllMemes 分支（重选 AllMemesVm，不写 LastCategory） |
| 8 | #16 新增"最近使用"分类 + 使用计数/时间 | 无最近使用视图；UsageCount 不持久化 | AllMemes 为虚分类范式可复制；`MemeMetaEntry` 无 UsageCount/LastUsedAt（CategoryMetadata.cs:6）；`IncrementUsageAsync` 只改内存不落盘（MemeDataEngine.cs:1105-1109）；多处使用入口未计数 | 加 `CategoryKind.Recent`+`RecentMemesVm`+虚 ListView 项；`MemeMetaEntry` 加字段并读写（5 处映射）；`IncrementUsageAsync` 落盘（防抖）；新增 `GetRecentMemes()` 查询；复制/打开/Mini 发送等入口补计数 |
| 9 | #15 全部表情排序混乱 | 全部表情视图顺序乱、每次启动可能不同 | `GetMemes(null)` 合并所有分类按各自 `Priority` 排（MemeDataEngine.cs:213-231）；`DateAdded` 未持久化（CategoryMetadata.cs 无字段，LoadAllMetadataCore:182-191 未恢复）导致重启后默认 UtcNow 乱序 | 全部表情视图给独立排序（按 `_categoryOrder` 分组再 Priority，或持久化 DateAdded 后按之排序）；持久化并恢复 DateAdded |

### 任务拆分与执行顺序建议

- **纯视觉/文案（低风险，可快速做）**：#1（复选框可见度）、#3（移除警告文本）。
- **Bug 修复（需手动验证）**：#2（复选框消失/残留）、#7（全部表情焦点）。
- **IO/配置优化（需验证行为不变）**：#4（数据目录限制）、#5（多余写入）。
- **功能新增（需设计+实现）**：#6（三按钮）、#8（最近使用+计数）、#9（排序）。

### 改动文件速查

- `Views/MainPage.xaml(.cs)`：复选框可见性（#1/#2）、AllMemes 焦点与虚分类项（#7/#8）、排序无关。
- `Views/MiniPage.xaml(.cs)`：Mini 同步计数（#8）。
- `Views/SettingsPage.xaml(.cs)` + `ViewModels/SettingsViewModel.cs`：移除警告（#3）、三按钮（#6）、数据目录限制（#4）。
- `Infrastructure/MemeDataEngine.cs`：写入去重（#5）、Remove appDir 限制（#4）、计数落盘+GetRecentMemes（#8）、DateAdded 持久化+排序（#9）。
- `Models/CategoryMetadata.cs` / `MemeModel.cs`：新增 UsageCount/LastUsedAt/DateAdded 字段（#8/#9）。
- `Models/CategoryKind.cs` / `Infrastructure/AppConstants.cs`：Recent 虚分类（#8）。
- `Strings/zh-CN|en-US/Resources.resw`：新增/删除文案 key。
