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

- [ ] **`_allMemesVm`**（5处）→ `AllMemesVm` ⏸ **暂缓至 Phase 1.4**
  - **原因**：它非纯状态——第702行 `_allMemesVm.Count = cache.Count` 直接写 `CategoryViewModel.Count`，且与 `LoadCategories` / `UpdateCategoryCounts` 刷新逻辑耦合（注释明说"触发 SelectionChanged → RefreshMemes → UpdateCategoryCounts 用到"）。
  - **何时做**：等 **Phase 1.3 把 `CategoryViewModel` 换成 CommunityToolkit `[ObservableProperty]`、`Count` 变为可通知属性之后**，在 **Phase 1.4（MainPage 剩余状态清理）** 单独作为一个 commit 收尾。不混入 1.3 的 VM 现代化 commit，保持语义单一。

**Phase 1.2 完成标志**：MainPage.xaml.cs 内所有纯 UI 状态字段已迁至 `MainViewModel`（除 `_allMemesVm` 按上述暂缓至 Phase 1.4）；code-behind 仅保留 UI 生命周期/事件/拖拽/预览浮窗逻辑；build 0 警告 0 错误。

> 注：原路线 1.2 提及的 `BatchBar.Visibility` 绑定、`SearchBox.Text` 绑定、`_previewTimer` 等属于"XAML 绑定化"，属后续 Phase 2/3 范畴，不在本次状态迁移内。

### 1.3 MemeViewModel / CategoryViewModel 换 CommunityToolkit（VM 内部现代化，不改业务逻辑）

> **纪律**：仅做 VM 内部机械升级（手写 INotifyPropertyChanged → ObservableObject + `[ObservableProperty]`），**不重写、不引入新逻辑**。每个 VM 独立 commit。

- [x] **1.3.1** `CategoryViewModel.cs` → `partial class CategoryViewModel : ObservableObject`，`Name`/`Count`/`IsSelected` 加 `[ObservableProperty]`；删除手写 `PropertyChanged` 事件与 `OnPropertyChanged` 方法 ✅
  - 验收：分类名修改、计数更新、选中状态 UI 自动刷新，无功能变化
- [ ] **1.3.2** `MemeViewModel.cs` → `partial class MemeViewModel : ObservableObject`
  - **只改** `Title`、`IsSelected` 为 `[ObservableProperty]`
  - **不改**：`Hash`/`LocalPath`/`Category`/`FileName`（只读转发属性，非状态，保留）
  - **保留**：`UpdateModel` 里对 `Hash`/`LocalPath`/`Category`/`FileName`/`Title`/`ImageSource` 的手动 `OnPropertyChanged` 调用（`ObservableObject` 基类提供该方法）
  - **不动**：`ImageSource`/`PreviewSource` 的惰性加载 + `LiveBitmapImageCount` 副作用（业务逻辑，非纯状态）
  - 验收：图片标题修改后 UI 刷新、选中状态正常、图片列表显示正常、复用 VM 换源正常
  - ⚠️ 类声明必须加 `partial`（Toolkit 是 source generator，非 partial 编译报错）

**Phase 1.3 完成标志**：两个 VM 均继承 `ObservableObject`，手写通知代码消除，UI 行为与改造前一致。

---

### 1.4 MainPage 剩余状态清理

- [ ] **1.4.1** 收尾 `_allMemesVm`（5处）→ `MainViewModel.AllMemesVm`
  - 前置：依赖 1.3.1 完成（`CategoryViewModel.Count` 已是可通知属性）
  - `MainViewModel` 加 `AllMemesVm` 属性（初始化 `new CategoryViewModel("", 0)`）；`MainPage` 删本地声明，5 处引用改 `ViewModel.AllMemesVm`
  - 验收：左侧"全部表情"项计数随分类内容实时更新，切换正常

**Phase 1 完成标志**：`MainPage.xaml.cs` 减少约 30% 字段声明，所有纯 UI 状态字段（含 `_allMemesVm`）都在 ViewModel 中；两个 VM 已完成 Toolkit 现代化。

---

## Phase 2：按钮事件 → RelayCommand（一次一个）

> **Phase 2 核心纪律（改造前与 AI 讨论确定，务必遵守）**：
> 1. **只迁移入口，不搬业务**：`Xxx_Click` 的方法体**原样**移到 VM 的 `[RelayCommand]` 方法里，逻辑一行不改。业务拆 Service 留到 Phase 3。否则会变成"把 102KB code-behind 平移成 100KB ViewModel"的假重构。
> 2. **不是所有事件都 Command 化**：仅"用户意图型操作"迁移（刷新/设置/Mini/编辑/全选/分类操作/表情操作/点击粘贴）。以下**保留 code-behind 或 Behavior**：`DragStarting`/`DragOver`/`Drop`/`PointerMoved`/`Loaded`/`SizeChanged`/`Composition` 等 UI 生命周期/拖拽事件。
>    - **拖拽事件处理定位（已与 AI 讨论确定）**：拖拽是"View 决策 + ViewModel 执行"的混合体，**不强行 Command/附加属性化**：
>      - `DragOver`（设 `AcceptedOperation`/`DragUIOverride.Caption`、临时开关 `CanReorderItems`）属纯 UI 协商 → **留在 code-behind**（它只读 VM 状态如 `DraggingMemes`/`IsAllMemesView`，不调业务方法）。
>      - `Drop` / `DragItemsCompleted` 负责"执行" → 在 code-behind 里用 `ImageDragHelper.CollectDropPathsAsync(e.DataView)` 把 `DataView` **萃取成纯 `List<string>`**（图片过滤 + Bitmap 落临时文件已在 `ImageDragHelper` 实现，复用、不重写），再交给 VM 命令/方法。VM 永远收纯数据，绝不引用 `Microsoft.UI.Xaml`/`DragEventArgs`。
>      - **不新增 `DragDropHelper` 附加属性**：现有 `ImageDragHelper` + code-behind `Drop` 已经是合规且解耦的写法（"事件留 View + 数据萃取在 Helper + 业务执行在 VM"），换附加属性绑定只换写法、不提升解耦度，反而重复萃取逻辑。**保持现状**。
>      - 分类移动/重排的目标（如 `targetCategoryName`、新顺序 `List<string>`）在 View 层从 `sender.DataContext`/集合顺序萃取为纯数据再传 VM，VM 不碰 `CategoryViewModel`/`DragEventArgs`。
> 3. **async 用 Toolkit**：含 IO/导入/删除的 Command 用 `[RelayCommand]` 标记 `async Task`，Toolkit 生成 `IAsyncRelayCommand`，**禁止 `async void`**。
> 4. **Command 参数用 VM 类型**：`[RelayCommand] void DeleteMeme(MemeViewModel meme)` 而非 `(object sender)`。让 `RoutedEventArgs` 消失，体现 MVVM 价值。
> 5. **MemeItem_Tapped 暂留 MainViewModel**（当前模板结构复杂），记一笔：未来应下沉到 `MemeViewModel`/`MemeItemViewModel`。
> 6. **右键菜单（MenuFlyout）绑定坑**：WinUI 的 `MenuFlyout`/`ContextFlyout` 不在视觉树内，传统 `Binding` 依赖 `DataContext` 继承 + `ElementName=RootGrid` 锚点，在**空白区域右键**时仍可能失效（2.7 实测），所以**页面级/空白菜单一律用 `x:Bind ViewModel.XxxCommand`**。但 **`DataTemplate` 内项级菜单** `x:Bind` 默认根是项 VM、够不到页面 VM，**标准写法就是用 `Binding`**：`Command="{Binding DataContext.XxxCommand, ElementName=RootGrid}"` + `CommandParameter="{Binding}"`。不要写成 `x:Bind ((MainPage)App.Current.Content)...` 那种绝对路径。详见 AGENTS.md「XAML 绑定规范（x:Bind 优先）」与「项级菜单」节。

按复杂度从低到高，每个按钮改完立即验证（build + 点击/功能点一遍）：

- [x] **2.1** `RefreshButton_Click` (line 1755) → `[RelayCommand] RefreshCommand`
  - 验证 async Command 管线 / loading 状态 / 异常兜底
  - 验收：点击刷新按钮 → 列表刷新，加载指示器正常
- [x] **2.2** `SettingsButton_Click` (line 1725) → `[RelayCommand]`
  - 验收：点击设置 → 设置浮窗弹出
- [x] **2.3** `MiniModeButton_Click` (line 1740) → `[RelayCommand]`
  - 验收：点击 Mini 按钮 → 切换到 Mini 模式
- [x] **2.4** `EditButton_Click` (line 886) → `[RelayCommand] ToggleEditModeCommand`
  - 验收：进入/退出多选模式正常
- [x] **2.5** `SelectAllButton_Click` (line 1523) → `[RelayCommand]`
  - 验收：全选/取消全选正常
- [x] **2.6** `AddCategoryButton_Click` (line 625) → `[RelayCommand]`
  - 验收：新建分类弹窗正常
- [x] **2.7** 分类右键菜单 (lines 285/292/313/319) → 各 `[RelayCommand]`（**本阶段是 MVVM ContextFlyout 模板，务必慢、不要批量改**；详见 AGENTS.md「DataTemplate / ContextFlyout 内绑定 Page VM 的 Command」与「XAML 绑定规范（x:Bind 优先）」）
  - **绑定写法（已定）**：
    - **页面级 / 空白区域菜单**（如 `ListView.ContextFlyout` 的新建分类、顶栏按钮）用 `{x:Bind ViewModel.XxxCommand}`，**不依赖 ElementName、不受 Flyout 脱离视觉树影响**（2.7 最初用 `Binding ElementName=RootGrid` 在空白右键失效，改 `x:Bind` 后解决）。
    - **项级菜单**（`DataTemplate x:DataType=CategoryViewModel` 内）：`x:Bind` 在模板里默认根是项 VM，无法够到页面 `ViewModel`，所以**标准写法就是用 `Binding`**：`Command="{Binding DataContext.OpenCategoryFolderCommand, ElementName=RootGrid}"` + `CommandParameter="{Binding}"` 传当前项。不要写成 `{x:Bind ((MainPage)App.Current.Content).ViewModel.XxxCommand}` 这种又长又吓人的绝对路径。
  - **不要**给 `CategoryViewModel` 加 `DeleteCommand`/`RenameCommand`——删/重命名/打开文件夹触及 `MemeDataEngine`+文件系统，属页面业务，放 `MainViewModel`，参数用 `CategoryViewModel` 类型。
  - **两个 ContextFlyout 区分**：`ListView.ContextFlyout`（空白区域菜单，无当前项）直接绑 `x:Bind ViewModel.NewCategoryCommand`，无 `CommandParameter`；`ListView.ItemTemplate` 内项级菜单才有 `CommandParameter="{Binding}"`（用 `Binding ElementName=RootGrid` 够到页面命令）。
  - **保留 `MenuFlyout Opening` 等 UI 生命周期事件**在 code-behind（设置当前右键对象/动态改菜单状态/判断能否删除），不强行 Command 化。
  - `CategoryOpenFolder_Click` → `OpenCategoryFolderCommand(CategoryViewModel)`
  - `CategoryNew_Click` → `NewCategoryCommand`（无参，走 ListView.ContextFlyout）
  - `CategoryDelete_Click` → `DeleteCategoryCommand(CategoryViewModel)`
  - `CategoryRename_Click` → `RenameCategoryCommand(CategoryViewModel)`
  - 验收：分类右键四个操作均正常；空白区域右键新建正常；`x:DataType` 警告消失且不靠给 CategoryViewModel 加 Command 消警告；页面已暴露强类型 `ViewModel` 属性供 `x:Bind` 解析。
  - ⚠️ 做对 2.7 后，2.8（`MemeViewModel` 右键菜单）直接复制本模式：项级 Command 用 `Binding DataContext.XxxCommand, ElementName=RootGrid` 够到 MainViewModel + `CommandParameter="{Binding}"` 传 `MemeViewModel`，方法 `DeleteMeme(MemeViewModel)` 等。
- [x] **2.8** 表情右键/批量按钮 (lines 1392–1633) → 各 `[RelayCommand]`（参数用 `MemeViewModel`，见纪律 4）
  - `MemeCopy_Click` → `CopyMemeCommand(MemeViewModel)`
  - `MemeDelete_Click` → `DeleteMemeCommand(MemeViewModel)`
  - `MemeOpen_Click` / `MemeOpenFolder_Click` → `OpenMemeCommand` / `OpenMemeFolderCommand`
  - `MemeRename_Click` → `RenameMemeCommand(MemeViewModel)`
  - `BatchImportButton_Click` → `BatchImportCommand`
  - `BatchExportButton_Click` → `BatchExportCommand`
  - `DeleteButton_Click` → `BatchDeleteCommand`
  - 验收：右键菜单和批量按钮全部正常
- [x] **2.9** `MemeItem_Tapped` (line 940) → `[RelayCommand]`（带 `MemeViewModel` 参数，见纪律 5 暂留 MainVM）
  - 验收：单击粘贴、Shift+点击多选正常

### 2.10 / 2.11 MiniPage 与 SettingsPage 命令化（Phase 2 扩展，方案 A）

> **背景**：MiniPage、SettingsPage 当前没有 ViewModel，所有按钮点击走 code-behind 的 `_Click`。按 Phase 2 纪律补齐——各新建一个 `MiniViewModel` / `SettingsViewModel`（DI 注册 `AddSingleton` + Page `DataContext = App.GetService<T>()` + 公开 `ViewModel` 属性供 `x:Bind`），把"明确用户意图"的按钮迁 `[RelayCommand]`。
>
> **方案 A 范围（只迁用户意图型操作，不碰配置绑定）**：
> - 新建 VM 与 Page 接通 DI（方式同 MainViewModel：Page 内 `App.GetService<T>()` 取，公开 `ViewModel` 属性；`x:Bind ViewModel.XxxCommand` 解析）。
> - **保留 code-behind**：Toggle 的 `Toggled`（多为"延后保存"空壳，仅 EcoMode 有即时逻辑）、热键录制"开始/取消"状态机（动态换 Click 处理）、文本框校验、拖入导入——这些属 UI 状态/Window 耦合，不是纯命令。
>
> **绑定规范**：页面级按钮用 `x:Bind ViewModel.XxxCommand`；若某命令需当前项参数（如 MiniPage 发送图片传 `MemeViewModel`），因不在 DataTemplate 内可直接 `x:Bind ViewModel.SendMemeCommand` + 参数用 `x:Bind`/`Binding` 取项（MiniPage 的 Picker 项用 `Tag` 承载 VM，需薄 Tapped 适配器调命令，参照 MainPage 2.9）。
>
> **⚠️ 单例 VM 事件订阅纪律（已踩坑修复，务必遵守）**：VM 用 `AddSingleton`，但 Page 每次打开/进入都会重新 `new` 一个实例。若 Page 在构造里把处理方法**订阅**到单例 VM 的 `Requested` 事件（如 `ExpandToFullRequested` / `SendToExternalRequested` / `BrowseFolderRequested` / `OpenFolderRequested` / `CloseRequested`），旧 Page 销毁时**必须反订阅**，否则每打开一次就累积一份处理器，第 N 次打开点一次按钮会触发 N 次。
> - 订阅理由：VM 不能反向依赖 `MainWindow`（否则 VM 无法独立测试、破坏 MVVM 边界）；VM 只发"请求"事件表达意图，由 Page 在订阅里调用 `App.MainWindow` 等窗口副作用。直接 `App.MainWindow.Xxx()` 写进 VM 是违规。
> - 反订阅做法：用**具名字段**保存处理器（匿名 lambda 无法 `-=`），在 `Page.Unloaded` 里 `-=` 对应事件并移除 `Unloaded` 自身。
> - 若今后某 VM 改成"每页一个实例"（非 singleton），此纪律可免，但本项目统一 singleton，故必须执行。

- [x] **2.10** MiniPage 命令化 ✅ 提交 `a8ac555`
  - 新建 `MiniViewModel.cs`（`ObservableObject`，构造器注入 `MemeDataEngine`）
  - `App.ConfigureServices()` 加 `services.AddSingleton<MiniViewModel>();`；MiniPage 构造器 `DataContext = App.GetService<MiniViewModel>();`；公开 `public MiniViewModel ViewModel => (MiniViewModel)DataContext;`
  - `ExpandButton` → `ExpandCommand`：命令发 `ExpandToFullRequested` 事件 → Page 调 `App.MainWindow.SwitchMode(AppMode.Full)`（Window 调用留 Page，VM 不依赖 Window）
  - `PickerItem_Tapped` 保留薄 Tapped 适配器转调 `SendMemeCommand(MemeViewModel)`；VM 发 `SendToExternalRequested(MemeViewModel)` 事件 → Page 调 `App.MainWindow.ResolveExternalPasteTarget()` + `PasteService.OutputMemeToCursorAsync`
    - 决策：`ResolveExternalPasteTarget` 是 `MainWindow` 实例方法，MiniViewModel 直接引用会引入 VM→Window 耦合，故**经事件**回 Page 执行（与 MainViewModel 同模式）。
  - XAML：`ExpandButton` 改 `Command="{x:Bind ViewModel.ExpandCommand}"`；`PickerItem` 的 `Tapped` 适配器调 `ViewModel.SendMemeCommand.Execute(vm)`
  - 验收：Mini 模式点 Expand 切回完整模式正常；Picker 点图片发送到外部窗口正常

- [x] **2.11** SettingsPage 命令化 ✅ 提交 `56effbd`
  - 新建 `SettingsViewModel.cs`（`ObservableObject`，构造器注入 `MemeDataEngine`）
  - `App.ConfigureServices()` 加 `services.AddSingleton<SettingsViewModel>();`；SettingsPage 接通 DataContext + 公开 `ViewModel` 属性
  - `OpenConfigFolderButton` → `OpenConfigFolderCommand`：纯 `Launcher.LaunchFolderPathAsync(MainWindow.AppDataDir)` 直接进 VM（仅依赖 `MainWindow.AppDataDir` 静态访问，不依赖窗口实例）
  - `OpenMemeDataFolder` → `OpenMemeDataFolderCommand(string path)`：路径来自 UI 文本框，**经事件** `OpenFolderRequested(string path)` → Page 调 `OpenFolderAsync(path)`
  - `BrowseButton` → `BrowseFolderCommand`：文件选择器 + `IsFilePickerOpen` + 立即保存 + `ReloadData` 全依赖 Page/Window，**整体经事件** `BrowseFolderRequested` → Page 执行
  - `CloseButton` → `CloseCommand`：关闭设置浮窗属 UI，**经事件** `CloseRequested` → Page 调 `SaveAndCloseAsync`
  - XAML：对应 Button 改 `Command="{x:Bind ViewModel.XxxCommand}"`（OpenMemeDataFolder 带 `CommandParameter="{x:Bind StoragePathBox.Text}"`）
  - **保留 code-behind**：7 个 Toggle 的 `Toggled`、`RecordHotKeyButton` 状态机、`StoragePathBox_TextChanged` 校验、各 `PreviewXxx_TextChanged` / `BeforeTextChanging`、语言/主题下拉——属 UI 状态/配置回填
  - 验收：设置页打开配置文件夹、打开数据文件夹、浏览选目录并立即保存刷新、点"完成/关闭"关闭浮窗，均正常

### 2.10/2.11 后续修复：单例 VM 事件订阅累积 ✅ 提交 `4932655`
- 根因：`MiniViewModel` / `SettingsViewModel` 是 `AddSingleton`，但 MiniPage / SettingsPage 每次进入/打开都重新 `new` 实例，构造里匿名 lambda 订阅的事件处理器逐次累积 → 第 N 次打开点按钮触发 N 次（如打开文件夹 N 次）。
- 修复：订阅改用**具名字段**，在 `Page.Unloaded` 里 `-=` 反订阅对应事件。两页都已改。
- 纪律已写入上方「单例 VM 事件订阅纪律」。
  - 提交：`refactor: 2.11 SettingsPage 命令化`

**Phase 2 完成标志（扩展后）**：MainPage / MiniPage / SettingsPage 三个页面的"用户意图型操作"按钮均迁入对应 ViewModel 的 `[RelayCommand]`（MainPage 已完成 2.1–2.9；MiniPage 2.10；SettingsPage 2.11）；UI 生命周期/拖拽/动态事件（Toggle Toggled、热键录制状态机、文本框校验、Opening 动态菜单等）仍保留在 code-behind。各页面明显瘦身且**未**变成"第二个巨型 VM"。

---

## Phase 3：按功能拆 Service

每拆一个 Service，MainPage 减少 200+ 行。顺序：独立 → 耦合。

### 3.1 搜索 → SearchService

- [x] 新建 `SearchService.cs`，搬入：
  - `SearchBox_TextChanged` 防抖逻辑
  - `RefreshMemes()` 中搜索过滤部分
  - `_searchDebounceTimer`
- [x] `MainViewModel` 注入 `SearchService`
- [x] 验收：搜索框输入关键词 → 列表过滤正常，清空搜索 → 恢复全量；防抖 150ms 行为不变
  - ✅ commit `579da69`

### 3.2 导入/导出 → ImportExportService（合并原 ImportService + 批量导出 + 粘贴导入）

- [x] 新建 `ImportExportService.cs`，搬入：
  - `RunBatchImportAsync()` 整段
  - `PasteFromClipboardViaShortcutAsync()` 的剪贴板读取 + 导入逻辑
  - `BatchExportButton_Click` 导出逻辑
  - `TryGuardWrite()` 写入锁守卫（提取为独立 helper 或放 Service 基类）
- [x] `MainViewModel` 注入 `ImportExportService`
- [x] 验收：批量导入按钮 → 进度条 → 完成后图片出现；Ctrl+V 粘贴导入正常；批量导出正常；写入锁并发守卫正常
  - ✅ commit `0983b05`（MainPage 实现 IImportExportUi；ImageBatchOperationRunner/BatchProgressHelper 改 public）

### 3.3 剪贴板 → ClipboardService（原 PasteService，改为实例注入）

- [x] `PasteService` 重命名为 `ClipboardService`（静态类 → 实例类），语义涵盖"复制剪贴板"与"发送到外部窗口"。
- [x] 注册到 DI 容器（`App.xaml.cs`）
- [x] `MainViewModel` / `MiniViewModel` 注入 `ClipboardService`
- [x] 验收：复制/粘贴发送功能不变
  - ✅ commit `3e66587`

### 3.4 删除 & 移动 & 重命名 → MemeOperationService（名字保留，不改 ImageInternal）

- [x] 新建 `MemeOperationService.cs`，搬入：
  - `DeleteSelectedMemesAsync()`
  - `MemeDelete_Click` 右键删除逻辑
  - `MoveMemeToCategory()` 移动逻辑
  - `MemeRename_Click` → `App.DataEngine.RenameMemeAsync()` 重命名逻辑
- [x] 验收：删除单张/批量 → 确认弹窗 → 图片移除；移动到其他分类正常；重命名正常
  - ✅ commit `e8c9907`（MainPage 实现 IMemeOperationUi；删除/移动/冲突守卫迁入，重命名留 VM）

### 3.5 分类管理 → CategoryService

- [x] 新建 `CategoryService.cs`，搬入：
  - `ShowAddCategoryDialog()` / `AddCategoryButton_Click`
  - `DeleteCategoryConfirmed()` / `CategoryDelete_Click`
  - `ShowRenameCategoryDialog()` / `CategoryRename_Click`
  - `LoadCategories()` / `UpdateCategoryCounts()`（注：LoadCategories 因强耦合 UI/当前视图保留在 Page；UpdateCategoryCounts 改调 ComputeCounts；新增 ComputeCounts 纯查询）
- [x] 验收：新建/删除/重命名分类正常，分类计数实时更新
  - ✅ commit `296eb3e`
  - ⚠️ 后续修复：重命名当前分类后选中高亮丢失（CurrentCategory 未跟随更新）✅ commit `e61aac4`

### 3.6 拖拽（保持现状，不重构为 Command/Service）

> **决策（已讨论）**：拖拽逻辑**不搬进 ViewModel、不新增 `DragDropHelper` 附加属性、不拆成独立 Service**。
> `ImageDragHelper`（拟改名 `ViewDragService`）是** View 层的 UI 适配器/胶水**（引用 `Windows.Storage`/`DataPackageView`，萃取 `DataView` → `List<string>`），
> 与 VM 解耦的边界已经清晰：code-behind 的 `Drop`/`DragOver`/`DragItemsCompleted` 调用它拿到纯数据后交给 VM。
> 强行抽成 Service 或附加属性只会重复萃取逻辑、不提升解耦度。

- [x] **保持 `ViewDragService`（原 `ImageDragHelper`）为 `static` 工具类（按 DI 决策清单本就 static 不动）**，不注册 DI。
- [x] 维持现状：各 Page code-behind 的 `Drop`/`DragOver`/`DragItemsCompleted` 事件 + `ViewDragService.CollectDropPathsAsync`/`ConfigureDragOut` 萃取纯数据 → VM 命令/方法。
- [x] 验收：从资源管理器拖图片入库、从 MemeManager 拖图片到 QQ、分类栏内部移动/重排，行为不变。
  - 注：QQ 富文本输入框拖入 / QQ 复制图片粘贴，因 TX 不暴露文件句柄（GetStorageItemsAsync 抛 COMException 或仅给 HTML/富文本格式），暂不做兜底，保持现状（非图片内容已有弹窗提示）。

**Phase 3 完成标志**：`MainPage.xaml.cs` 从 ~2300 行缩减到 ~500 行（只剩 UI 生命周期 + 拖拽事件 + 预览浮窗），ViewModel 约 200 行（纯命令转发）。

---

## Phase 4：拆分 MemeDataEngine（经评估 => 不做全量大拆，仅抽 ConfigService）

> **结论（与用户讨论确定，2026-08）**：
> `MemeDataEngine` 本身作为**数据访问层（Repository/DAO）** 职责已自洽——它只管"图片数据 + 元数据读写"，不依赖 UI/ViewModel、已单例化、经 DI 注入（`App.GetService<MemeDataEngine>()`）。Phase 3 已将业务编排迁到 5 个 Service（Search/ImportExport/Clipboard/MemeOperation/Category），形成"Service=业务编排、Engine=数据持久化"的清晰分层。再往下把 Engine 内部的"文件 IO + 缓存更新 + metadata"这三件天然纠缠的事硬撕开，只会增加缓存一致性风险（如删除计数类问题）、运行行为零变化、用户无感——属过度工程。**故 Phase 4 的"抽 MemeRepository / 把写操作迁入各 Service / 引擎降级 Facade"三项目标不做。**
>
> **唯一保留的拆分：`ConfigService`**（见 4.2）——因为应用配置（`AppConfig` 读写、`StoragePath`、分类顺序文件路径解析）与"图片数据/元数据"是两回事，且调用方不止引擎（设置页、MainPage 都改配置），挤在引擎里边界不清。抽离后引擎只关心数据目录 `_baseDir`，不再持有 `Config` 对象，且配置不碰 `_memeCache`、零缓存风险，是最干净的软柿子。

- [x] **4.1** ~~从 `MemeDataEngine` 抽 `MemeRepository`~~ —— **不做**（Engine 作为数据层已满足解耦，见上方结论）
- [ ] **4.2** 配置管理独立 → `ConfigService`（**保留，做**）：
  - 从 `MemeDataEngine` 抽出：`AppConfig` 字段、`LoadConfig()`、`SaveConfigAsync()`、`UpdateConfigAsync()`、`ConfigDir`/`ConfigPath`/`CategoryOrderPath`（配置/分类顺序文件路径解析）
  - `ConfigService` 注册 DI（`AddSingleton`），引擎与设置页/MainPage 改注入 `ConfigService`
  - 引擎保留 `_baseDir`（数据目录），`InitializeAsync` 从 `ConfigService` 取 `StoragePath` 解析 `_baseDir`
  - 验收：设置页修改 `StoragePath`/配置 → 重启后保留；`LastCategory` 写入正常；分类顺序持久化正常；`dotnet build` 0 警告 0 错误
- [x] **4.3** ~~写操作迁入各 Service~~ —— **不做**（见结论）
- [x] **4.4** ~~引擎降级 Facade~~ —— **不做**（引擎作为数据访问层保留现状）

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
    │   ├── ViewDragService.cs      ← 原 ImageDragHelper（View 层拖拽适配器，static）
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
    │   ├── ImportExportService.cs    ← 原 ImportService + 批量导出 + 粘贴导入
    │   ├── ClipboardService.cs       ← 原 PasteService（复制剪贴板 + 发到外部窗口）
    │   ├── MemeOperationService.cs
    │   └── CategoryService.cs
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
| 7 | `Views/ImageDragHelper.cs`（拟改名 `ViewDragService`） | 2 | 静态类，**保持 static 不动**（View 层 UI 适配器，非 Service） | ✅ 按决策不动 |
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
| **ImageDragHelper**（拟改名 `ViewDragService`） | static class | 5 / 2 | | ❌ 保持 static（View 层 UI 适配器，非 Service，不进容器） |
| **TrayIcon** | ? | 4 / 1 | | ✅ 可选注册单例 |
| **PasteService**（拟改名 `ClipboardService`） | static class | 3 / 2 | | ✅ Phase 3 改实例类并 `AddSingleton` |
| **StartupManager** | static class | 3 / 2 | | ❌ 保持 static（启动期一次性） |
| **FileWatcher** | DataEngine 成员 | 3 / 1 | | ✅ 随 DataEngine 注入，不单独注册 |

**决策清单（做完一个勾一个）**

- [ ] `MemeDataEngine` → 注册为 `AddSingleton`，全项目通过构造器注入获取
- [ ] `Localization` → 保持 static，不进容器
- [ ] `Logger` → 保持 static，不进容器
- [ ] `LangHelper` → 保持 static，不进容器
- [ ] `EcoQos` → 保持 static，不进容器
- [ ] `Utils` → 保持 static，不进容器
- [ ] `ImageDragHelper`（拟改名 `ViewDragService`） → 保持 static（View 层 UI 适配器，非 Service，不进容器；见 Phase 3.6 决策）
- [ ] `TrayIcon` → 注册为 `AddSingleton`（可选）
- [ ] `PasteService`（拟改名 `ClipboardService`） → Phase 3 改为实例类并 `AddSingleton`
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
- [ ] **DI-final**：全局 grep `App.DataEngine` 仅剩 Logger/EcoQos/LangHelper/ImageDragHelper（按决策保持 static，含 ImageDragHelper/ViewDragService 不再 Phase 3 实例化的转发引用）的转发引用 → `App.DataEngine` 静态属性**暂不删除**（仍有 static 文件依赖转发）；待确认无残留业务调用后评估

> **注入方式说明**：Page / Window 由 XAML 导航或框架实例化，无法走构造器注入，统一用字段初始化
> `private readonly MemeDataEngine _engine = ((App)Application.Current).Services.GetRequiredService<MemeDataEngine>();`
> 并 `using Microsoft.Extensions.DependencyInjection;`。构造器可注入的类（Service/ViewModel/策略类）则用构造器参数。

**完成标志**：`ConfigureServices()` 就绪且各 Service/ViewModel 已注册；所有业务代码通过构造器注入获取 `MemeDataEngine`；`App.DataEngine` 静态入口已删除；项目仍可正常运行。
