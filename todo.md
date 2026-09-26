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

# 单元测试覆盖计划（减少手动测试工作）

> 现状：`MemeManager.Tests`（xUnit）已有 51 用例（SafePath / FileNameValidator，路径安全核心）。
> 目标：把核心数据层 + 业务服务 + VM 命令纳入单测，UI/系统级（拖拽、剪贴板发送、托盘、P/Invoke）不测。

## 覆盖方案（按优先级）

| 模块（文件 · 行数） | 可测性 | 预估用例数 | 预估测试代码量 | 优先级 | 备注 |
| --- | --- | --- | --- | --- | --- |
| **MemeDataEngine**（Infrastructure · 1022） | ✅ 高 | **35–45** | 1200–1800 行 | **P0** | 临时目录驱动；需 InternalsVisibleTo + 存储路径注入 |
| **MainViewModel**（ViewModels · 239） | ✅ 高 | **15–25** | 500–800 行 | **P1** | 注入临时目录引擎 + 假委托（PromptRenameMemeRequested 等）；OpenMeme/OpenFolder 不测（进程） |
| **Utils**（Infrastructure · 145） | ✅ 高 | **8–12** | 200–250 行 | **P1** | FitWithin / PlacePopup / ClassifySize 纯几何；OpenInExplorer 不测 |
| **SearchService**（Services · 30） | ✅ 高 | **5–8** | 100–150 行 | **P2** | 搜索/标签过滤，纯 LINQ |
| **ConfigService**（Services · 74） | 🟡 中 | **5–8** | 150–200 行 | **P2** | 需支持注入 ConfigPath（当前硬编码 %LOCALAPPDATA%） |
| **FileWatcher**（Infrastructure · 154） | 🟡 中 | **4–6** | 150–200 行 | **P2** | ToChange / ShouldTrack 纯逻辑（InternalsVisibleTo）；FSW 时序不测 |
| **LangHelper**（Infrastructure · 176） | 🟡 中 | **4–6** | 100–150 行 | **P2** | 语言列表构建/索引，纯逻辑 |
| **MemeOperationService**（Services · 120） | 🟡 中 | **3–5** | 100–150 行 | **P2** | 排除依赖 runner 的部分 |
| **ReuseStrategy**（Models · 126） | 🟡 中 | **3–5** | 80–120 行 | **P3** | 策略决策逻辑，先确认是否纯逻辑 |
| **SettingsViewModel**（ViewModels · 76） | 🟡 中 | **3–5** | 80–120 行 | **P3** | 事件触发（About/Browse/Close Requested）；Launcher 类不测 |
| **AppConfig**（Models · 72） | ✅ 高 | **2–3** | 40–60 行 | **P3** | record 默认值/值相等 |
| **合计** | | **~90–110 用例** | **~2700–4000 行** | | |

## 明确不测

| 模块 | 原因 |
| --- | --- |
| ClipboardService / TrayIcon / Views 全部 | 剪贴板、托盘、拖拽、XAML——UI/系统级 |
| NativeMethods / HotKey / EcoQos | P/Invoke + 线程/进程 API |
| StartupManager | 写真实注册表（HKCU Run），测试污染系统 |
| Logger / Localization / AppConstants | 静态 + App 依赖，无逻辑 |
| CategoryService / CategoryViewModel / MiniViewModel / MemeModel / 枚举类 | 薄包装/POCO，测试价值低 |

## 后续优化（勿忘）

- **抽离 `MemeManager.Core` 独立类库**（P1，解除测试对 WinUI 的依赖）：
  - 现状：`MemeManager.Tests` 引用 WinUI 3 主项目 → testhost 加载主项目程序集触发 WinUI XAML `[ModuleInitializer]`，需要 WindowsAppRuntime 运行时（CI 必须每次安装、启动慢，本地缺 runtime 也会挂）。
  - 目标：把纯逻辑代码（SafePath / FileNameValidator / 未来的 MemeDataEngine、Service 逻辑等）抽成 `MemeManager.Core`（纯 .NET，不引用 WinUI），`MemeManager` 主项目与 `MemeManager.Tests` 都引用它；测试只依赖 Core → 秒级启动、CI 无需装 runtime。
  - 范围：按"UI 无关"边界抽（Infrastructure 的纯逻辑部分 + Models + Service 逻辑），Views/Clipboard/TrayIcon/P-Invoke 留在主项目。
  - 现有 51 用例（SafePathTests / FileNameValidatorTests）届时改为引用 Core。

- **复杂多选导出子窗口**（分类多选 + 跨分类 meme 批量导出，待评估未开始）：
  - 背景：主界面做"分类三态复选框 ↔ meme 全局跨分类选中 ↔ 批量操作"联动太过复杂、状态耦合（切虚拟分类"全部表情"会难以处理）。
  - 方案：新增独立子窗口承载复杂多选导出，子窗口内多选分类 → 预览/勾选 meme → 导出，状态完全隔离，不污染主界面 meme 多选与 CurrentCategory。
  - 待定：入口（批量导出按钮/右键）、分类多选交互、预览是否可二次勾选、导出参数、虚拟分类排除规则。

- **封装 `MemeDataCache`（数据层缓存整理，待开工）**：
  - 动机：`MemeDataEngine` 里三个缓存字段——`List<MemeModel> _memeCache`、`Dictionary<string, List<string>> _titleReverseMap`（title→文件名列表）、`Dictionary<string, uint> _categoryOrder`（分类名→优先级，越大越靠前）——互相之间存在必须同步的不变量（图片改名要同时改 cache 与标题反查索引；分类改名/删除要同步顺序表）。现在这些散在 1000+ 行引擎代码里靠人肉维护，字段一多/结构一变，上游查询很容易漏同步。
  - 顺带解掉性能担忧：`GetCategories()` 现在为了收集分类名要 `_memeCache.ToList()` + 全量 foreach（万级图片就是万级遍历），`ComputeCounts()` 同理。Cache 内部维护「分类名集合 / 计数」后，两者都降为 O(分类数)。
  - 形态：新建 `Models/MemeDataCache.cs`（纯内存结构，可脱 UI/磁盘单测），按「内容 × 操作」提供入口：
    - 图片：`AddMeme` / `ReplaceMeme`（按 hash 或 fileName 去重）/ `RemoveMeme` / `RemoveMemes(category)` / `UpdateMeme`（Title / Priority / Category）/ `GetMemeByHash` / `GetAllMemes`（返回快照拷贝）/ `GetMemes(category, keyword)`。
    - 分类：`GetCategories`（有序：优先级降序 + 同名稳定）/ `AddCategory` / `RemoveCategory` / `RenameCategory` / `SetCategoryOrder(names)` / `GetCategoryCount(name)` / `ReloadCategories`（合并磁盘上含 `.metadata.json` 的目录）。
    - 查询第三类：`ReverseLookupByTitle(title)`——派生索引只由内部增删改维护，外部永不直接碰 `_titleReverseMap`。
  - 边界铁律：Cache 只管「内存结构 + Reload（读盘）」，所有写盘 IO（`SaveCategoryMetadataAsync` / `SaveCategoryOrderAsync`）仍留在 `MemeDataEngine`，别让 Cache 变成第二层引擎。
  - 线程模型先定死并写进类注释：现状无锁，靠「UI 线程写 + EcoQos 后台读 + FileWatcher 回调」的约定；入口变多后误用面变大，选其一——内部统一一把锁，或硬性声明「仅 UI 线程可写」。
  - `ReloadMeme(category)` 暂缓：按分类重载会与 FileWatcher 增量事件、导入/删除流程交叉，容易出现「重载冲掉刚增量写的条目」；先只留 `ReloadAll` + 细粒度增删改。
  - 迁移节奏（两个 commit，别混着做）：
    1. **零行为变化的搬运**：字段 + 维护代码搬进 Cache，`MemeDataEngine` 方法体改为转发调用（引擎保留路径解析 / 权限 / 写盘 / 事件 / FileWatcher / EcoQos 编排）；逐处核对 1300 行里每个字段读写的语义（哪些取快照、哪些是写），`_memeCache.ToList()` 的快照拷贝语义必须保留。
    2. **再做性能优化**：`GetCategories()` / `ComputeCounts()` 改走内部的分类名集合与计数。
  - 测试：`MemeDataCache` 是纯内存结构，不需要临时目录 / `InternalsVisibleTo` 那套前置改造即可 xUnit 覆盖——分类改名/删除时顺序表与计数同步、图片改名时标题反查的旧键清理 + 新键建立、`GetAllMemes` 返回拷贝（外部改动不污染内部）、`GetCategories` 排序稳定性。这些不变量目前只能靠手测。
  - 关系：与上一条「抽离 `MemeManager.Core`」同向——Cache 属 UI 无关的纯逻辑，抽 Core 时一并迁入。
