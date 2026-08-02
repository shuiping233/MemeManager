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

---

## MVVM 组织思路与纪律

### 1. 依赖注入（DI）

- 容器：`Microsoft.Extensions.DependencyInjection`；Page/Window 由框架实例化，用字段式
  `App.GetService<T>()` 取（Service Locator 过渡方案，可接受）。
- 生命周期：全局单例用 `AddSingleton`；需要每实例隔离的才 `AddTransient`。
- `MemeDataEngine` 进容器（核心数据层）；`Localization`/`Logger`/`LangHelper`/`EcoQos`/`Utils`
  保持 static（无状态工具，强行进容器会让 100+ 处调用改签名，得不偿失）；`ViewDragService`
  保持 static（View 层 UI 适配器，非 Service）；`FileWatcher` 作为 Engine 成员随其注入，不单独注册。

### 2. XAML 绑定规范（x:Bind 优先）

- 新代码默认用 `x:Bind`，传统 `Binding` 仅在 `x:Bind` 不适合时用。
- **页面级 / 空白区域菜单**：一律 `Command="{x:Bind ViewModel.XxxCommand}"`，不依赖 `ElementName`、
  不受 Flyout 脱离视觉树影响。
- **DataTemplate 内项级菜单**：`x:Bind` 默认根是项 VM，够不到页面 VM，标准写法用
  `Command="{Binding DataContext.XxxCommand, ElementName=RootGrid}"` + `CommandParameter="{Binding}"`
  传当前项。不要写 `x:Bind ((MainPage)App.Current.Content)...` 绝对路径。
- 页面必须暴露强类型 `ViewModel` 属性（`public MainViewModel ViewModel => (MainViewModel)DataContext;`），
  供 `x:Bind` 编译期解析；不要写 `{x:Bind DataContext.SomeCommand}`。
- 不要给项 VM（`CategoryViewModel`/`MemeViewModel`）加删除/重命名等触及 DataEngine/文件系统的 Command——
  这类操作属页面业务，放 `MainViewModel`，参数用项 VM 类型。

### 3. VM → Page 的事件桥接纪律（单例 VM 必须反订阅）

VM 需触发依赖窗口/视觉树/XamlRoot 的副作用（弹文件选择器、切窗口模式、关浮窗等）时，**不能**在 VM 里
直接写 `App.MainWindow.Xxx()`（破坏 MVVM 边界、VM 无法独立测试）。正确做法：VM 只发"请求"事件
（`XxxRequested`），由 Page 在订阅里调用 `App.MainWindow` 等具体实现。

⚠️ **单例 VM + 重建 Page 的订阅累积坑**：VM 统一 `AddSingleton`，但 Page 每次打开/进入都重新 `new`。
若 Page 在构造里把处理方法订阅到单例 VM 事件，旧 Page 销毁时**必须反订阅**，否则每打开一次累积一份
处理器，第 N 次打开点一次按钮触发 N 次。

规范：
- 用具名字段保存处理器（匿名 lambda 无法 `-=`），不写 `ViewModel.XxxRequested += async () => ...`。
- 在 `Page.Unloaded` 里 `-=` 对应事件并移除 `Unloaded` 自身。
- 例外：纯系统 API（如 `Launcher.LaunchFolderPathAsync`）不依赖窗口实例，可直接进 VM。

### 4. RelayCommand 命名与用法

- 命名按"用户意图"，不按控件名（`RefreshCommand` 不是 `RefreshButtonClickCommand`）。
- 方法参数用 VM 类型（`DeleteMeme(MemeViewModel meme)`），不要 `object sender`；多参数包装成请求类。
- 返回 `void` → `IRelayCommand`；异步返回 `Task`（生成 `IAsyncRelayCommand`）；长耗时用
  `[RelayCommand(IncludeCancelCommand = true)]` 支持取消。
- `CanExecute`：`[RelayCommand(CanExecute = nameof(CanXxx))]` + 同名 `bool CanXxx()`；某
  `[ObservableProperty]` 变化时要刷新命令可用态，标 `[NotifyCanExecuteChangedFor(nameof(XxxCommand))]`。
- 类必须 `partial`（source generator 要求）；方法可以是 `private`。

### 5. UI 生命周期事件保留 code-behind

- `Opening` / `Loaded` / `DragOver` / `Drop` / `DragItemsStarting` / `DragItemsCompleted` /
  `PointerMoved` / `ContainerContentChanging` 等属 UI 生命周期或拖拽视觉协商，**留在 code-behind**，
  不强行 Command 化。
- 用户动作 Click/Tapped → 迁 Command（VM）；UI 生命周期事件 → 留 Page。

### 6. 拖拽逻辑不重构决策

拖拽是"View 决策 + ViewModel 执行"混合体，不搬进 VM、不新增 `DragDropHelper` 附加属性、不拆独立 Service。
`ViewDragService` 作为 View 层适配器，把 `DataView` 萃取成纯 `List<string>`（图片过滤 + Bitmap 落临时文件），
code-behind 的 `Drop`/`DragItemsCompleted` 拿纯数据后交给 Service/VM。强行抽成 Service 或附加属性只重复
萃取逻辑、不提升解耦度。

### 7. 不做的过度工程

- `MemeDataEngine` 作为数据访问层职责已自洽，不再往下硬拆 Repository / 写操作迁入 Service / 引擎降级 Facade
  （只会增加缓存一致性风险、运行行为零变化、用户无感）。唯一抽离的是 `ConfigService`（配置与图片数据是两回事）。

---

## 重构收尾结论（2026-08）

**MVVM 重构已到顶，无必须做的架构重构剩余。**

- Phase 0–4 已完成：业务逻辑迁 5 个 Service、按钮命令化（Phase 2）、ViewModel 状态化（Phase 1）。
- R3 单例事件累积泄漏已根除：18+2 个 VM→Page 请求事件从 `event` 改为委托属性（`Action`/`Func<...>`），Page 用 `=` 赋值，覆盖式天然不累积（顺手修了 Mini 切几次后 Expand 按钮失效的实锤 bug）。
- 分层已清晰：Infrastructure（数据）/ Services（业务编排）/ ViewModels（状态+命令）/ Views（UI 接线）四层职责分明。

**MainPage(~2000) / MiniPage(325) / SettingsPage(424) 剩下的代码主体是 View 层天职**：拖拽、悬停预览 Popup、选中态镜像、窗口生命周期（IImageReleasablePage/IExternalDropPage）、配置 Toggle 回调、快捷键路由、文件监听转发。这些不是"没搬完的业务逻辑"，是 UI 的本质工作，留 Page 才符合 MVVM（VM 不引用 Microsoft.UI.Xaml、不操控控件）。

**可做的纯整洁度项（可选，不改变行为/分层）**，想收拾再收拾，不做也不影响架构：
- 删死注释、合并 `OpenSettingsFlyout` 空壳、内联 `EnterEditModeAndSelectAll`、统一 `AllMemesCategory` 常量到 VM。
- 抽 `CategoryMenuBuilder` 收敛两处"移动到分类"动态菜单（仅该子菜单重复，异构固定菜单项不抽 UserControl，YAGNI）。
- 少量纯逻辑可进 VM：`ResolveInitialSelection`（算初始选中）、`IsCategoryExists`（重名校验）、写锁忙判断。
- 高风险项（分类/表情重排写回搬 Service、MemeItem_Tapped 改 Command）因 R1/R2 风险，维持现状。
