# MemeManager 重构路线

> **总目标**：让 `MainPage.xaml.cs`（2323行/102KB）只负责页面，让 `MemeDataEngine`（~1000行）只负责数据。
>
> **原则**：已有架构演进。项目已有 `MemeManager.Models` / `MemeManager.ViewModels` / `MemeManager.Data` / `MemeManager.Helpers`
> 分层，延续这个方向。每步保证 `dotnet build` 通过 + 功能可运行，一步一 commit。

---

## FileWatcher 分类事件（FileWatcher.cs）

当前 `FileWatcher` 有文件级事件（`FilesRemoved`/`FilesAdded`/`FilesMoved`，均已在 MainPage 订阅），
但缺少分类层级变更探测：

- [ ] **`CategoryRemoved`** — 分类文件夹被删 → `MainWindow`/`MainPage` 移除左侧分类项 + 清理计数
  - 验收：资源管理器删除分类文件夹 → 左侧栏该分类消失，计数更新
- [ ] **`CategoryAdded`** — 新建分类文件夹 → `MainWindow`/`MainPage` 在左侧栏追加
  - 验收：资源管理器新建分类文件夹 → 左侧栏出现新分类
- [ ] **`CategoryRenamed`** — 分类文件夹改名 → `MainWindow`/`MainPage` 同步分类名
  - 验收：资源管理器重命名分类文件夹 → 左侧栏分类名更新

---

## Phase 0：画职责图（0 行代码改动）

- [ ] **0.1** 打开 `MainPage.xaml.cs`，标注所有 **UI 状态字段**（目前散落在 24–86 行）：

  | 字段 | 类型 | 归属 |
  |---|---|---|
  | `_memeList` | `ObservableCollection<MemeViewModel>` | → ViewModel |
  | `_categoryList` | `ObservableCollection<CategoryViewModel>` | → ViewModel |
  | `_allMemesVm` | `CategoryViewModel` | → ViewModel |
  | `_currentCategory` | `string` | → ViewModel |
  | `_currentKind` | `CategoryKind` | → ViewModel |
  | `_editMode` | `bool` | → ViewModel |
  | `_listStrategy` | `IMemeListStrategy` | → ViewModel |
  | `_dragAnchorFileName` | `string?` | → ViewModel |
  | `_draggingMemes` | `List<MemeModel>?` | → ViewModel |
  | `_reloading` | `bool` | → ViewModel |
  | `_lastShiftAnchor` | `int` | → ViewModel |
  | `_pasteDialogOpen` | `bool` | → ViewModel |
  | `_previewTimer` / `_pendingPreviewVm` / `_pendingPreviewAnchor` | 悬停预览 | → ViewModel |
  | `_searchDebounceTimer` | `DispatcherTimer?` | → ViewModel |
  | `_contextMeme` / `_contextCategory` | 右键上下文 | → ViewModel |
  | `_previewFadingOut` / `_lastPointerPos` / `_suppressNextMove` | 预览浮窗 | 留在 code-behind（纯 UI） |

- [ ] **0.2** 标注所有 **业务方法 → 后续拆入 Service**：

  | 方法 | 后续归属 |
  |---|---|
  | `RunBatchImportAsync()` | → `ImportService` |
  | `DeleteSelectedMemesAsync()` | → `MemeOperationService` |
  | `MoveMemeToCategory()` | → `MemeOperationService` |
  | `BatchExportButton_Click()` | → `MemeOperationService` |
  | `MemeDelete_Click()` | → `MemeOperationService` |
  | `MemeRename_Click()` → `App.DataEngine.RenameMemeAsync()` | → `MemeOperationService` |
  | `MemeCopy_Click()` → `PasteService.CopyImageToClipboardAsync()` | → `PasteService`（已有，改注入） |
  | `PasteFromClipboardViaShortcutAsync()` | → `ImportService` |
  | `ShowAddCategoryDialog()` / `DeleteCategoryConfirmed()` / `ShowRenameCategoryDialog()` | → `CategoryService` |
  | `CategoryNew_Click()` / `CategoryDelete_Click()` / `CategoryRename_Click()` | → `CategoryService` |
  | `SearchBox_TextChanged()` / `SearchDebounce_Tick()` / `RefreshMemes()` | → `SearchService` |
  | `LoadCategories()` / `UpdateCategoryCounts()` | → `CategoryService` |

- [ ] **0.3** 标注所有 **留在 code-behind 的 UI 生命周期**：

  | 方法 | 理由 |
  |---|---|
  | `MemeItem_PointerEntered` / `_Exited` | 悬停预览浮窗定位，纯 UI |
  | `Root_PointerMoved` | 浮窗跟踪鼠标，纯 UI |
  | `ShowPreviewPopup` / `HidePreviewPopup` / `FadeInPreview` | 浮窗动画，纯 UI |
  | `MemeGridView_DragItemsStarting` / `_Completed` / `_DragOver` / `_Drop` | 拖拽 DataPackage 操作，纯 UI |
  | `CategoryList_DragOver` / `_DragItemsCompleted` / `CategoryListItem_Drop` | 分类拖拽排序，纯 UI |
  | `EditButton_Click` / `EnterEditMode` / `ExitEditMode` | 编辑模式 UI 切换 |
  | `TopMostToggle_Checked` / `_Unchecked` | 置顶开关 |
  | `Root_KeyDown`（快捷键路由） | 键盘事件分发 |

  验收：Phase 0 完成后，你有一份手写/脑图中的职责清单，后续每步都知道"这个该搬、那个不该搬"。

---

## Phase 1：搬 UI 状态到 ViewModel（不改业务逻辑）

> **关键顺序**：先搬状态，后拆 Service——否则 Service 会开始依赖 UI 状态。

### 1.1 创建 MainViewModel 空壳 + 接通 DI（方式 A，不改动 Navigation）

> **决策（Phase 1.1 前与 AI 讨论确定）**：
> - Page 由 WinUI `Frame.Navigate(typeof(Page))` 内部 `Activator.CreateInstance` 实例化，**不能走构造器注入**。
> - 采用**方式 A**：Page 内通过 `App.GetService<T>()` 取 ViewModel（Service Locator 过渡方案，可接受）。
> - 不改动 Navigation、不改 `OnNavigatedTo`、不拆 Service、不新增业务逻辑迁移。
> - `App.Services` 保持**实例属性**（承认有状态），新增 `static App.GetService<T>()` 封装，内部走 `((App)Current).Services`，以后换 DI 框架只动 `App`。

- [ ] `App` 加 `static T GetService<T>() where T : class => ((App)Current).Services.GetRequiredService<T>();`
- [ ] 把已完成的字段式注入 `((App)Application.Current).Services.GetRequiredService<X>()` 批量改为 `App.GetService<X>()`（MiniPage / MainPage / SettingsPage / TrayIcon 调用处等，代码量小，统一清理）
- [ ] `ConfigureServices()` 加 `services.AddSingleton<MainViewModel>();`
- [ ] 新建 `MainViewModel.cs`，继承 `ObservableObject`，带空构造器（未来加 `MemeDataEngine` 参数时 diff 清晰）
  ```csharp
  public partial class MainViewModel : ObservableObject
  {
      public MainViewModel()
      {
      }
  }
  ```
- [ ] `MainPage` 构造函数中 `DataContext = App.GetService<MainViewModel>();`（保持 `RootFrame.Navigate(typeof(MainPage))` 不变）
- [ ] `dotnet build` 通过 → 独立 commit `refactor: Phase 1.1 创建 MainViewModel 空壳并接通 DI`

**验收标准**：
- ✅ App 启动正常
- ✅ MainPage `DataContext` 类型为 `MainViewModel`
- ✅ 原 Click 事件 / `App.DataEngine` 调用不受影响
- ✅ 无新增业务逻辑迁移（只接通 DI，不搬任何字段/方法）

### 1.2 搬简单状态字段（纯 UI 状态，不搬业务逻辑）

> **执行纪律（Phase 1.2 实际采用）**：从大到小逐字段迁移，每个字段独立 commit；只搬纯状态读写，不碰任何业务方法；`MainPage` 用 `ViewModel => (MainViewModel)DataContext` 访问 VM。

- [x] **1.2.1** `_currentCategoryKind`（6处）→ `CurrentCategoryKind` ✅ commit `1fad586`
- [x] **1.2.2** `_currentCategory`（25处）→ `CurrentCategory` ✅ commit `efed747`
- [x] **1.2.3** `_editMode`（23处）→ `EditMode` ✅ commit `d20f9de`
- [x] **1.2.4** `_memeList`(27) + `_categoryList`(26) → `MemeList` / `CategoryList`（ObservableCollection 属性）✅ commit `6555a29`
- [x] **1.2.5** `_draggingMemes`（23处）→ `DraggingMemes` ✅ commit `40365c8`
- [x] **1.2.6** `_contextMeme`(13) + `_contextCategory`(10) → `ContextMeme` / `ContextCategory` ✅ commit `e9a18a4`
- [x] **1.2.7** `_reloading`(7) + `_searchDebounceTimer`(7) + `_pendingPreviewVm`(7) → `Reloading` / `SearchDebounceTimer` / `PendingPreviewVm` ✅ commit `8320d7b`
- [x] **1.2.8** `_dragAnchorFileName`(6) + `_pendingPreviewAnchor`(6) + `_pasteDialogOpen`(4) + `_lastShiftAnchor`(3) → 对应属性 ✅ commit `8c9a785`

- [ ] **1.2.9** `_allMemesVm`（5处）→ `AllMemesVm` ⏸ **暂缓**
  - **原因**：它非纯状态——第702行 `_allMemesVm.Count = cache.Count` 直接写 `CategoryViewModel.Count`，且与 `LoadCategories` / `UpdateCategoryCounts` 刷新逻辑耦合（注释明说"触发 SelectionChanged → RefreshMemes → UpdateCategoryCounts 用到"）。
  - **何时做**：等 **Phase 1.3 把 `CategoryViewModel` 换成 CommunityToolkit `[ObservableProperty]`、`Count` 变为可通知属性之后**，再回头搬。届时计数刷新链路已稳，搬移不会破坏 UI 更新。归到 Phase 1.3 收尾项。

**Phase 1.2 完成标志**：MainPage.xaml.cs 内所有纯 UI 状态字段已迁至 `MainViewModel`（除 `_allMemesVm` 按上述暂缓）；code-behind 仅保留 UI 生命周期/事件/拖拽/预览浮窗逻辑；build 0 警告 0 错误。

> 注：原路线 1.2 提及的 `BatchBar.Visibility` 绑定、`SearchBox.Text` 绑定、`_previewTimer` 等属于"XAML 绑定化"，属后续 Phase 2/3 范畴，不在本次状态迁移内。

### 1.3 MemeViewModel / CategoryViewModel 换 CommunityToolkit

- [ ] `MemeViewModel.cs`：手动 `INotifyPropertyChanged` → 继承 `ObservableObject`，字段加 `[ObservableProperty]`
  - 验收：图片标题修改后 UI 自动刷新，无功能变化
- [ ] `CategoryViewModel.cs`：同上
  - 验收：分类名修改/计数更新后 UI 自动刷新
- [ ] **Phase 1.2.9 收尾**：`CategoryViewModel` 改造完成后，补搬 `_allMemesVm`（5处）→ `MainViewModel.AllMemesVm`（见 1.2 暂缓说明）
  - 验收：左侧"全部表情"项计数随分类内容实时更新，切换正常

**Phase 1 完成标志**：`MainPage.xaml.cs` 减少约 30% 字段声明，所有纯 UI 状态字段都在 ViewModel 中（`_allMemesVm` 已在 1.2.9 收尾搬完）。

---

## Phase 2：按钮事件 → RelayCommand（一次一个）

按复杂度从低到高，每个按钮改完立即验证：

- [ ] **2.1** `RefreshButton_Click` (line 1755) → `[RelayCommand] RefreshCommand`
  - 验收：点击刷新按钮 → 列表刷新，加载指示器正常
- [ ] **2.2** `SettingsButton_Click` (line 1725) → `[RelayCommand]`
  - 验收：点击设置 → 设置浮窗弹出
- [ ] **2.3** `MiniModeButton_Click` (line 1740) → `[RelayCommand]`
  - 验收：点击 Mini 按钮 → 切换到 Mini 模式
- [ ] **2.4** `EditButton_Click` (line 886) → `[RelayCommand] ToggleEditModeCommand`
  - 验收：进入/退出多选模式正常
- [ ] **2.5** `SelectAllButton_Click` (line 1523) → `[RelayCommand]`
  - 验收：全选/取消全选正常
- [ ] **2.6** `AddCategoryButton_Click` (line 625) → `[RelayCommand]`
  - 验收：新建分类弹窗正常
- [ ] **2.7** 分类右键菜单 (lines 285/292/313/319) → 各 `[RelayCommand]`
  - `CategoryOpenFolder_Click` → `OpenCategoryFolderCommand`
  - `CategoryNew_Click` → `NewCategoryCommand`
  - `CategoryDelete_Click` → `DeleteCategoryCommand`
  - `CategoryRename_Click` → `RenameCategoryCommand`
  - 验收：分类右键四个操作均正常
- [ ] **2.8** 表情右键/批量按钮 (lines 1392–1633) → 各 `[RelayCommand]`
  - `MemeCopy_Click` → `CopyMemeCommand`
  - `MemeDelete_Click` → `DeleteMemeCommand`
  - `MemeOpen_Click` / `MemeOpenFolder_Click` → `OpenMemeCommand` / `OpenMemeFolderCommand`
  - `MemeRename_Click` → `RenameMemeCommand`
  - `BatchImportButton_Click` → `BatchImportCommand`
  - `BatchExportButton_Click` → `BatchExportCommand`
  - `DeleteButton_Click` → `BatchDeleteCommand`
  - 验收：右键菜单和批量按钮全部正常
- [ ] **2.9** `MemeItem_Tapped` (line 940) → `[RelayCommand]`（带参数绑定）
  - 验收：单击粘贴、Shift+点击多选正常

**Phase 2 完成标志**：MainPage 上所有 `_Click` / `_Tapped` 事件处理方法变成 ViewModel 中的 `[RelayCommand]`，XAML 绑定 `Command="{Binding ...}"`。

---

## Phase 3：按功能拆 Service

每拆一个 Service，MainPage 减少 200+ 行。顺序：独立 → 耦合。

### 3.1 搜索 → SearchService

- [ ] 新建 `SearchService.cs`，搬入：
  - `SearchBox_TextChanged` 防抖逻辑
  - `RefreshMemes()` 中搜索过滤部分
  - `_searchDebounceTimer`
- [ ] `MainViewModel` 注入 `SearchService`
- [ ] 验收：搜索框输入关键词 → 列表过滤正常，清空搜索 → 恢复全量；防抖 150ms 行为不变

### 3.2 导入 → ImportService

- [ ] 新建 `ImportService.cs`，搬入：
  - `RunBatchImportAsync()` 整段（lines 1561–1611）
  - `PasteFromClipboardViaShortcutAsync()` 的剪贴板读取 + 导入逻辑
  - `TryGuardWrite()` 写入锁守卫（提取为独立 helper 或放 Service 基类）
- [ ] `MainViewModel` 注入 `ImportService`
- [ ] 验收：批量导入按钮 → 进度条 → 完成后图片出现；Ctrl+V 粘贴导入正常；写入锁并发守卫正常

### 3.3 剪贴板/发送 → PasteService（已有，改为实例注入）

- [ ] `PasteService` 从 `static` 改为实例类
- [ ] 注册到 DI 容器（`App.xaml.cs`）
- [ ] `MainViewModel` 注入 `PasteService`
- [ ] 验收：复制/粘贴发送功能不变

### 3.4 删除 & 移动 → MemeOperationService

- [ ] 新建 `MemeOperationService.cs`，搬入：
  - `DeleteSelectedMemesAsync()` (line 1637+)
  - `MemeDelete_Click` 右键删除逻辑 (line 1406)
  - `MoveMemeToCategory()` 移动逻辑
  - `MemeRename_Click` → `App.DataEngine.RenameMemeAsync()` 重命名逻辑
  - `BatchExportButton_Click` 导出逻辑 (line 1613)
- [ ] 验收：删除单张/批量 → 确认弹窗 → 图片移除；移动到其他分类正常；重命名正常；导出正常

### 3.5 分类管理 → CategoryService

- [ ] 新建 `CategoryService.cs`，搬入：
  - `ShowAddCategoryDialog()` / `AddCategoryButton_Click`
  - `DeleteCategoryConfirmed()` / `CategoryDelete_Click`
  - `ShowRenameCategoryDialog()` / `CategoryRename_Click`
  - `LoadCategories()` / `UpdateCategoryCounts()`
- [ ] 验收：新建/删除/重命名分类正常，分类计数实时更新

### 3.6 拖拽 → ImageDragHelper（已有，改为实例注入）

- [ ] `ImageDragHelper` 从 `static` 改为实例类，注册 DI
- [ ] `CollectDropPathsAsync()` 和 `ConfigureDragOut()` 通过注入调用
- [ ] 验收：从资源管理器拖图片入库、从 MemeManager 拖图片到 QQ，行为不变

**Phase 3 完成标志**：`MainPage.xaml.cs` 从 ~2300 行缩减到 ~500 行（只剩 UI 生命周期 + 拖拽事件 + 预览浮窗），ViewModel 约 200 行（纯命令转发）。

---

## Phase 4：拆分 MemeDataEngine（风险最高，最后做）

当前 `MemeDataEngine`（~1000行）= Repository + Service + Config。Phase 3 的各 Service 仍直接依赖 `App.DataEngine`，Phase 4 把它们切到 `MemeRepository`：

- [ ] **4.1** 从 `MemeDataEngine` 抽 `MemeRepository`：
  - 搬入：`_memeCache`、`_titleReverseMap`、`GetAllMemes()`、`GetMemes()`、`GetCategories()`、`ReverseLookupByTitle()`、`AddCategoryAsync()`、metadata 读写、`LoadAllMetadataCore()`
  - 暴露为只读查询接口，写操作（`ImportMemesAsync`、`DeleteMemesAsync` 等）暂留 `MemeDataEngine`
  - 验收：`dotnet build` → 分类/图片展示正常
- [ ] **4.2** 配置管理独立：
  - `AppConfig` 加载/保存从 `MemeDataEngine` 抽出 → `ConfigService`（或直接放 `Infrastructure`）
  - 验收：设置页修改配置 → 重启后保留
- [ ] **4.3** 写操作（导入/删除/移动/重命名/导出）逐步从 `MemeDataEngine` 迁入对应 Service
  - 验收：每迁一个写操作，对应功能正常
- [ ] **4.4** `MemeDataEngine` 降级为 Facade：只转发调用，不自己干活
  - 为保持兼容可以让旧调用方继续用，内部转发到新 Service/Repository
  - 验收：所有功能正常，`MemeDataEngine` 行数缩减到 ~100 行

---

## 最后：目录整理（Phase 0–4 全部完成后一次性做）

分两步：先建 `MemeManager/` 子文件夹把项目嵌套进去，再在内部按功能分层。

`Helpers/` 不再保留——这些文件全是 UI 相关，直接归入 `Views/`。

### 目标结构

```
Repository                         ← 仓库根（.sln 在这）
│
├── MemeManager.sln
├── README.md
├── .github/
├── docs/
├── scripts/
├── tests/
│
└── MemeManager/                   ← 项目根（.csproj 在这）
    │
    ├── MemeManager.csproj
    ├── App.xaml / .cs
    ├── Assets/
    ├── Strings/
    ├── Properties/
    │
    ├── Views/                     ← XAML + code-behind + UI 辅助
    │   ├── MainWindow.xaml / .cs
    │   ├── Pages/
    │   │   ├── MainPage.xaml / .cs
    │   │   ├── SettingsPage.xaml / .cs
    │   │   └── MiniPage.xaml / .cs
    │   ├── Controls/
    │   │   └── LocalizedToggleSwitch.xaml / .cs
    │   ├── Dialogs/
    │   │   └── DialogHelper.cs
    │   ├── ImageDragHelper.cs
    │   ├── ImageBatchOperationRunner.cs
    │   ├── BatchProgressHelper.cs
    │   ├── PickerHelper.cs
    │   ├── IExternalDropPage.cs
    │   └── IImageReleasablePage.cs
    │
    ├── ViewModels/
    │   ├── MainViewModel.cs
    │   ├── MemeViewModel.cs
    │   ├── CategoryViewModel.cs
    │   ├── SettingsViewModel.cs
    │   └── MiniViewModel.cs
    │
    ├── Models/
    │   ├── MemeModel.cs
    │   ├── CategoryMetadata.cs
    │   ├── CategoryOrderMetadata.cs
    │   ├── AppConfig.cs
    │   ├── MemeListStrategy.cs
    │   ├── RebuildStrategy.cs
    │   └── ReuseStrategy.cs
    │
    ├── Services/
    │   ├── SearchService.cs
    │   ├── ImportService.cs
    │   ├── MemeOperationService.cs
    │   ├── CategoryService.cs
    │   └── PasteService.cs
    │
    ├── Infrastructure/            ← 横切关注点
    │   ├── MemeDataEngine.cs
    │   ├── FileWatcher.cs
    │   ├── Logger.cs
    │   ├── EcoQos.cs
    │   ├── Localization.cs
    │   ├── LangHelper.cs
    │   ├── StartupManager.cs
    │   ├── NativeMethods.cs
    │   ├── TrayIcon.cs
    │   └── Utils.cs
    │
    ├── Converters/
    │   └── BoolToVisibilityConverter.cs
    │
    ├── Behaviors/                 ← 空目录，后续放拖拽/Pointer 行为
    │
    └── Extensions/                ← 空目录，后续放扩展方法
```

### 执行步骤

- [x] **Step 1**：建 `MemeManager/` 子文件夹，移入 `.csproj`、所有源码/XAML、`Assets/`、`Strings/`、`Properties/`
- [x] **Step 2**：更新 `MemeManager.sln` 中项目路径为 `MemeManager\MemeManager.csproj`，`dotnet build` 通过（0 警告 0 错误）为 `MemeManager\MemeManager.csproj`，`dotnet build` 验证
- [x] **Step 3**：源文件已按上表拖入各子文件夹，命名空间已调整为 `MemeManager.Views` / `.ViewModels` / `.Services` / `.Infrastructure` 等（物理分层完成，逻辑尚未拆分）
- [ ] **Step 4**：`dotnet build` + 完整功能回归（搜索/导入/删除/移动/拖拽/Mini模式/设置/快捷键）

---

## 前置准备：搭建 DI 容器（Phase 1 之前必做，单独 commit）

> **目标**：在动手改 XAML / code-behind 之前，先把依赖注入骨架立好，让后续每个 ViewModel / Service 都能用构造器注入，而不是 `App.DataEngine` 静态全局。

### 选型约定

- 容器：`Microsoft.Extensions.DependencyInjection`（成熟、全生命周期支持；若已引用 CommunityToolkit.Mvvm 也可改用其 `Ioc.Default`，二选一，早定）
- **Page / Window 不进容器**：XAML `partial class` 由框架实例化最自然，避免在容器里 `new` 带 XAML 的类型
- **注入方式**：ViewModel / Service 进容器；Page 通过**构造器注入 ViewModel**，不在 XAML 里 `new ViewModel()`
- 生命周期：全局单例用 `AddSingleton`；需要每实例隔离的才用 `AddTransient`

### 落地模板

**App.xaml.cs 骨架：**

```csharp
public partial class App : Application
{
    public IServiceProvider Services { get; }

    public App()
    {
        Services = ConfigureServices();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        // 基础设施 / 数据层
        services.AddSingleton<MemeDataEngine>();
        services.AddSingleton<FileWatcher>();
        // 业务 Service（Phase 3 逐步补）
        services.AddSingleton<PasteService>();
        // ViewModel（Phase 1 起逐步补）
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider();
    }
}
```

**Page 构造器注入（推荐模式）：**

```csharp
public MainPage(MainViewModel vm)
{
    InitializeComponent();
    DataContext = vm;
}
// 创建处：
var page = new MainPage(App.Services.GetRequiredService<MainViewModel>());
```

### 迁移策略：以文件为单位，逐个消灭 `App.DataEngine`

> **原则**：不在一次提交里改完 100+ 处。先立 DI 骨架（保留 `App.DataEngine` 作为容器转发入口，零风险），然后**按文件逐个**把 `App.DataEngine` 改为构造器注入的实例字段，改完一个文件即提交，该文件对 `App.DataEngine` 的引用必须归零。

**节奏**
1. 先提交 DI 骨架：`App.Services` + `ConfigureServices()` + 注册 `MemeDataEngine` / `FileWatcher` 等无依赖基础设施；`App.DataEngine` 暂留（先不标 `[Obsolete]`，否则 100 处全冒警告）。
2. 之后**按文件**逐个迁移：
   - 给该文件所属类（ViewModel / Page / Helper）增加构造器参数 `MemeDataEngine engine`，存为 `private readonly` 字段。
   - 把文件内所有 `App.DataEngine.Xxx` 换成注入字段 `engine.Xxx`（注意 `MemeDataEngine.UncategorizedCategory` 这类静态成员仍走类名，不动）。
   - `dotnet build` 通过 + 对应功能点一遍 → commit（标题带文件名，如 `refactor: DI 注入 MemeViewModel`）。
3. 全部文件清完后，`App.DataEngine` 静态属性应只剩声明本身 → 直接删除，收尾提交。

**迁移顺序（从叶子到根，先易后难）**：被依赖少的底层文件先改，验证注入模式；大文件最后照抄。

| 顺序 | 文件 | `App.DataEngine` 引用数 | 说明 | 状态 |
|---|---|---|---|---|
| 1 | `Views/MiniPage.xaml.cs` | 10 | Page，由框架导航实例化，故用字段 `((App)Current).Services.GetRequiredService` 注入 | ✅ 已做(DI-1) |
| 2 | `ViewModels/MemeViewModel.cs` | 1 | 随 MainPage 批次一并注入：构造器加 `MemeDataEngine engine`，牵连 `RebuildStrategy`/`ReuseStrategy`（加 engine 参数）+ MiniPage new 传参 | ✅ 已做(DI-MainPage) |
| 3 | `Infrastructure/Logger.cs` | 2 | 保持 static（不进容器，见决策清单），内部 `App.DataEngine?` 容错保留 | ✅ 按决策不动 |
| 4 | `Infrastructure/EcoQos.cs` | 2 | 保持 static，不进容器 | ✅ 按决策不动 |
| 5 | `Infrastructure/LangHelper.cs` | 2 | 保持 static，不进容器 | ✅ 按决策不动 |
| 6 | `Infrastructure/TrayIcon.cs` | 1 | 实例化由 App `new TrayIcon(hwnd, engine)` 注入 | ✅ 已做(DI-MainWindow 批次) |
| 7 | `Views/ImageDragHelper.cs` | 2 | 静态类 → Phase 3 改实例类并注册 DI | ⏸ Phase 3 |
| 8 | `Views/SettingsPage.xaml.cs` | 7 | Page，字段式注入 | ✅ 已做(DI-2) |
| 9 | `Views/MainWindow.xaml.cs` | 14 | Window，构造器注入 `MemeDataEngine engine` | ✅ 已做(DI-MainWindow 批次) |
| 10 | `Views/MainPage.xaml.cs` | 47 | 字段式注入；策略类/VM 注入随此一并处理 | ✅ 已做(DI-MainPage) |

> **MainViewModel 构造器注入前置（做 MainPage 时一并完成）**：
> 当前 MainPage 仍 `new MainViewModel()`，故 `MainViewModel(MemeDataEngine engine)` 构造器注入**暂不支持**。
> 做 MainPage 那一步时需先：
> 1. `ConfigureServices()` 补 `services.AddSingleton<MainViewModel>();`
> 2. MainPage 改为从容器取：`public MainPage(MainViewModel vm) { InitializeComponent(); DataContext = vm; }`，删除原 `new MainViewModel()`
> 3. 之后 `MainViewModel` 构造器即可 `public MainViewModel(MemeDataEngine engine)` 由 M.E.DI 自动注入
> 此前置未完成前，不要给 MainViewModel 加构造器参数。

### DI 大户分类：哪些进容器、哪些保持 static

> **结论**：真正的 DI 大户只有 `MemeDataEngine`。其余被大量引用的对象多为**无状态静态工具类**，强行进容器会让 100+ 处调用全改签名，得不偿失——**保持 static 不动**。

| 对象 | 类型 | 被引用 | 分布 | 处理决策 |
|---|---|---|---|---|
| **MemeDataEngine** | 实例(=App.DataEngine) | ~100 | 多文件 | ✅ **进容器**（核心，必须） |
| **Localization** | static class | 86 / 11 | 多 | ❌ 保持 static（无状态工具） |
| **Logger** | static class | 85 / 16 | 多 | ❌ 保持 static（内部已 `App.DataEngine?.` 容错） |
| **LangHelper** | static class | 27 / 4 | | ❌ 保持 static（无状态） |
| **EcoQos** | ? | 7 / 3 | | ❌ 保持 static（无状态） |
| **Utils** | static class | 7 / 2 | | ❌ 保持 static（无状态） |
| **ImageDragHelper** | static class | 5 / 2 | | ✅ Phase 3 改实例注入（需传参） |
| **TrayIcon** | ? | 4 / 1 | | ✅ 可选注册单例 |
| **PasteService** | static class | 3 / 2 | | ✅ Phase 3 改实例注入 |
| **StartupManager** | static class | 3 / 2 | | ❌ 保持 static（启动期一次性） |
| **FileWatcher** | DataEngine 成员 | 3 / 1 | | ✅ 随 DataEngine 注入，不单独注册 |

**决策清单（做完一个勾一个）**

- [ ] `MemeDataEngine` → 注册为 `AddSingleton`，全项目通过构造器注入获取
- [ ] `Localization` → 保持 static，不进容器
- [ ] `Logger` → 保持 static，不进容器
- [ ] `LangHelper` → 保持 static，不进容器
- [ ] `EcoQos` → 保持 static，不进容器
- [ ] `Utils` → 保持 static，不进容器
- [ ] `ImageDragHelper` → Phase 3 改为实例类并 `AddSingleton`
- [ ] `TrayIcon` → 注册为 `AddSingleton`（可选）
- [ ] `PasteService` → Phase 3 改为实例类并 `AddSingleton`
- [ ] `StartupManager` → 保持 static，不进容器
- [ ] `FileWatcher` → 作为 `MemeDataEngine` 成员随其注入，不单独注册

**迁移纪律（防遗忘 / 防 DI 形同虚设）**
- 每个文件改完即**该文件零 `App.DataEngine` 引用**；不允许多数改了、少数留着。
- 禁止在已被重构进 ViewModel/Service 的代码中新增 `App.DataEngine` 调用。
- 全部文件清零后，删除 `App.DataEngine` 静态属性；届时若还有遗漏引用，build 会直接报错提示。
- （可选收尾）清零前可在 `App.DataEngine` 上加 `[Obsolete("改用 App.Services.GetRequiredService<MemeDataEngine>()")]`，让残留调用冒警告。

### 首批提交清单

- [x] **DI-0**：`App` 加 `Services` + `ConfigureServices()`，注册 `MemeDataEngine`（FileWatcher 随其注入不单独注册）；`App.DataEngine` 暂留作容器转发；build 通过 → commit `4a33511`
- [x] **DI-1**：`Views/MiniPage.xaml.cs` 字段式注入，清零 10 处 → commit `0624cfe`
- [x] **DI-2**：`Views/SettingsPage.xaml.cs` 字段式注入，清零 7 处 → 同批次或独立 commit
- [x] **DI-MainWindow 批次**：`MainWindow` 构造器注入 + `TrayIcon` 构造器注入（App new 处传参），清零 14+1 处 → commit
- [x] **DI-MainPage 批次**：`MainPage` 字段式注入（47处）+ `MemeViewModel`/`RebuildStrategy`/`ReuseStrategy` 构造器注入 + `MiniPage` new 传参，全部清零 → commit
- [ ] **DI-final**：全局 grep `App.DataEngine` 仅剩 Logger/EcoQos/LangHelper/ImageDragHelper（按决策保持 static 或 Phase 3）的转发引用 → `App.DataEngine` 静态属性**暂不删除**（仍有 static 文件依赖转发）；待 Phase 3 完成 ImageDragHelper 后评估

> **注入方式说明**：Page / Window 由 XAML 导航或框架实例化，无法走构造器注入，统一用字段初始化
> `private readonly MemeDataEngine _engine = ((App)Application.Current).Services.GetRequiredService<MemeDataEngine>();`
> 并 `using Microsoft.Extensions.DependencyInjection;`。构造器可注入的类（Service/ViewModel/策略类）则用构造器参数。

**完成标志**：`ConfigureServices()` 就绪且各 Service/ViewModel 已注册；所有业务代码通过构造器注入获取 `MemeDataEngine`；`App.DataEngine` 静态入口已删除；项目仍可正常运行。
