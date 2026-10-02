# MemeManager 重构路线

## 当前文件架构

```
MemeManager/
├── App.xaml / .cs                       ← 入口；DI 容器 (App.GetService<T>())
├── Views/                               ← XAML + code-behind + UI 辅助（平铺，无 Pages/Dialogs 子目录）
│   ├── MainWindow.xaml / .cs            ← 窗口壳；标题栏、模式切换、键盘/拖入转发、隐藏/退出生命周期
│   ├── MainPage.xaml / .cs              ← 完整模式页面（业务 UI 接线 + 批量/预览/拖拽）
│   ├── MiniPage.xaml / .cs              ← Mini 模式页面（分类 ComboBox + Picker 浮窗）
│   ├── SettingsPage.xaml / .cs          ← 设置页（DI 单例，作为设置浮窗内容；整棵树常驻，无 x:Load）
│   ├── AboutPage.cs                     ← 关于页（代码构建）
│   ├── DialogHelper.cs                  ← 确认/输入/提示弹窗（需 XamlRoot）
│   ├── ImageDragHelper.cs               ← View 层拖拽适配器（static）
│   ├── ImageBatchOperationRunner.cs     ← 批量操作编排（后台化+进度条+写锁+UI 收尾）
│   ├── BatchProgressHelper.cs           ← 顶部 InfoBar 进度条封装
│   ├── PickerHelper.cs                  ← 文件/文件夹选择器
│   ├── LocalizedToggleSwitch.xaml / .cs ← 带 i18n 的 ToggleSwitch（全项目唯一 UserControl）
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
。

- **复杂多选导出子窗口**（分类多选 + 跨分类 meme 批量导出，待评估未开始）：
  - 背景：主界面做"分类三态复选框 ↔ meme 全局跨分类选中 ↔ 批量操作"联动太过复杂、状态耦合（切虚拟分类"全部表情"会难以处理）。
  - 方案：新增独立子窗口承载复杂多选导出，子窗口内多选分类 → 预览/勾选 meme → 导出，状态完全隔离，不污染主界面 meme 多选与 CurrentCategory。
  - 待定：入口（批量导出按钮/右键）、分类多选交互、预览是否可二次勾选、导出参数、虚拟分类排除规则。

---

# 任务：主窗口隐藏后 UI 全量 teardown（WinUI 3 极限内存释放）

> **目标**：让 `MainPage` 在窗口隐藏后**真正死亡**（`RootFrame.Content == null` 且被 GC），
> 并把剩余内存**归因到 framework baseline 或我方残留**，而不是继续给控件打补丁。
>
> **先读 §0**：它决定这个任务该做到哪一层，以及"整页卸载"值不值得做。

## 0. 归因前置（必须先做，否则可能白干）

### 0.1 一个关键认知：托管堆不是主战场

| 指标 | 现状 | 来源 |
| --- | --- | --- |
| Managed Heap | 几 MB | `MainPage.xaml.cs:947` 诊断日志 |
| 关闭后专用工作集 | ~200MB（README）/ GC 后 ~270MB（注释实测） | `README.md:32`、`MainWindow.xaml.cs:629-633` |
| `LiveBitmapImageCount` | 隐藏后应趋 0 | `MemeViewModel.cs:175` |

托管堆只有几 MB 是**正常的**：WinUI 3 的 `GridView` / `Image` / `Popup` / `TextBlock` 在 C# 侧只是
native 对象的薄包装（RCW），真正的占用在 Microsoft.UI.Xaml 的 native 对象树、Composition/D3D 资源、
DWrite 字体缓存上。**因果链是：**

```text
断托管引用 → native 引用计数归零 → native XAML 对象析构 → native 内存才下降
```

所以本任务断托管引用，**不是为了省那几 MB 托管堆，而是拿托管引用当"总闸"去关 native 内存**。

### 0.2 但收益上限要提前认清

`x:Load` 已经卸载了最重的两块（分类 ListView、表情 GridView 及其容器池，
`MainPage.xaml:138,285`）。剩下 x:Load **外**的（TopBar 6 按钮 / BatchBar 5 按钮 + Flyout /
PreviewPopup 子树 / TitleStrip / CategorySplitter / BatchProgressInfoBar）native 量级较小。

```text
=> 整页卸载相比现状的额外收益「可能是几 MB 级」，不是能改变 200MB 的量级。
=> 真正的大头更可能是「框架基线」：只要窗口还活着（SW_HIDE 也算活着），
   Composition / D3D 设备与交换链、DWrite 字体缓存、XAML 元数据、
   WinAppSDK/.NET 的 DLL 代码页就不会释放 —— 这部分对"卸载控件树"几乎零响应。
```

### 0.3 一次性人工归因（决定后续值不值得做）

1. 启动 → 加载一个 1000+ 张图的分类 → 用 **VMMap**（Sysinternals）导出 Private Bytes 分类
   （Image / Mapped File / Private Data / Managed Heap / Shareable），逐栏记录。
2. 点 X 隐藏 → 等 GC 完成 → 再导一次做差。
3. 结论必须回答「还剩多少可压空间」：
   - 若差值里 **Image / Mapped** 是大头 → 已到框架基线，本任务只需做 Working Set trim（阶段 5）。
   - 若 **Private Data** 仍明显 → 还有 native 对象树没死，整页卸载（阶段 2/3）值得做。

### 0.4 实测基线（2026-10-02）与结论 ★

用阶段 0 落地后的诊断日志 + dotnet-counters 实采：

| 时点 | Managed | Private Bytes | Working Set | LiveBitmapImages | PageAlive |
| --- | --- | --- | --- | --- | --- |
| BeforeHide | 6.2 MB | 219.1 MB | 337.0 MB | 84 | — |
| AfterTeardown | 3.8 MB | 214.9 MB | 333.9 MB | 0 | True |
| 第二次 BeforeHide | 6.0 MB | 228.3 MB | 347.5 MB | 72 | — |
| 第二次 AfterTeardown | 3.9 MB | 229.4 MB | 348.7 MB | 0 | True |
| 第三次 BeforeHide | 6.0 MB | 262.2 MB | 386.1 MB | 72 | — |
| 第三次 AfterTeardown | 4.5 MB | 259.7 MB | 383.7 MB | 0 | True |

dotnet-counters：`GC Heap Size 6.27 MiB`、`LOH 745 KiB`、`Working Set 295 MiB`。

**结论（决定后面做什么）：**

1. **图片这块已回收干净，且量级很小**：`LiveBitmapImages 84 → 0`，Private 只降 ~4.2 MB，
   正好等于 84 张 120×120 缩略图的原生位图（120×120×4 ≈ 57.6 KB × 84 ≈ 4.8 MB）→ 无残留。
2. **托管堆只占 Private 的 ~3%**（6 MB / 215 MB）→ 印证 §0.1：省托管堆不是目标。
3. **teardown 的收益被噪声淹没**：Private 在多次隐藏之间自身就抖 ±30 MB（第二次反而涨了 1.1 MB），
   比"控件树能省的那几 MB"大一个数量级 → **继续在控件层抠，测不出收益**。
4. **~210 MB 在托管之外、且不随控件卸载移动** → 属框架/驱动/镜像页基线。
   （`Working Set 337 MB > Private 219 MB`，说明约 120 MB 是 file-backed 镜像/共享页。）
5. `PageAlive=True` 是预期值（现状下 `RootFrame.Content` 仍强引用着页面）。

**因此：**
- 阶段 2/3（整页卸载）**优先级下调**：预期收益几 MB 级且被噪声淹没，除非 §0.3 的 VMMap 归因
  显示 Private Data 里还有未死的大块。
- 阶段 5（Working Set trim）**提前**：它是唯一能显著改变"任务管理器内存列"的手段 ——
  目标正是那约 120 MB 的镜像/共享页。

### 0.5 第二轮实测：延迟 trim 生效（2026-10-02，`WorkingSetTrimDelay = 5s`）★

| 时点 | Managed | Private | Working Set |
| --- | --- | --- | --- |
| 启动空载（列表未加载） | 2.8 MB | **81.9 MB** | 169.6 MB |
| 加载 72 张图后隐藏前 | 5.6 MB | 207.6 MB | 321.3 MB |
| AfterTeardown | 4.3 MB | 204.6 MB | 319.1 MB |
| AfterWorkingSetTrim（5s 后） | 4.0 MB | **141.8 MB** | **5.5 MB** |
| 呼出后 AfterShow | 5.5 MB | 145.5 MB | 56.9 MB |

**结论（其中第 2 条修正 §0.4 的判断）：**

1. **EmptyWorkingSet 有效且效果持久**：WS 321 MB → **4~11 MB**；呼出后只回升到 30~57 MB（不是弹回 320 MB）。
2. ⚠️ **修正 §0.4**：上节说"EmptyWorkingSet 不释放 Private Bytes"**是错的** —— 实测每次 trim 后
   Private 都降约 60 MB（~25%）。机制未确认 → 已把 trim 拆成 `CompactManagedHeap()`（GC）与
   `EmptyProcessWorkingSet()` 两步、分别打点 `AfterCompactGc` / `AfterWorkingSetTrim`，
   下一轮日志可直接定位是哪一步释放的。
3. **空载基线 ~82 MB Private**，加载 72 张图后变 207 MB → **"图片列表" ≈ 125 MB Private / 152 MB WS**，
   远大于 84 张缩略图位图的 4.8 MB。而 `AfterTeardown` 只回收 ~4 MB（x:Load 摘掉 GridView 之后）
   → **这 125 MB 里绝大部分不在控件树的生命周期里**（框架的容器回收池 / glyph cache / Composition /
   WIC 解码池），这正是"WinRT 不愿意回收"的部分：XAML 只提供 `UnloadObject`（释放对象树）与 LRU，
   不提供"立刻归还框架池"的 API。
4. **隐藏态本身几乎不增长**：50 秒隐藏期间 Private 145.0 → 146.6 MB（+1.6 MB）。所以"每秒 WS 缓涨 +
   页面错误增加"是 trim 的正常后续（页留在 standby，被访问即软错误回到工作集），**不是泄漏**。
5. **真正值得追的是"呼出 → 加载列表"循环**：多次循环后 trim 基线从 145 MB 抬到 182~231 MB。
   需要受控循环实验判定是线性泄漏还是稳态。

**后续实验（按信息量排序）：**

| # | 实验 | 怎么做 | 产出 |
| --- | --- | --- | --- |
| E1 | trim 分步归因 | 看新日志 `AfterCompactGc` 与 `AfterWorkingSetTrim` 的差值 | 那 ~60 MB 是 GC 还是 EmptyWorkingSet 释放的 |
| E2 | 受控循环 | 呼出→等列表加载→隐藏→等 trim，重复 10 轮，每轮记快照 | Private 是线性增长（泄漏）还是收敛到稳态 |
| E3 | 图片数量线性度 | **零代码**：用 8 张图的分类与 200 张图的分类各跑一次，比较 Private 增量 | 成比例 → 每张图的 native 成本（可优化）；不成比例 → 一次性框架池（属基线） |
| E4 | 空闲长跑 | 隐藏 10 分钟，比较各时点快照 | 确认隐藏态不涨（已被结论 4 初步证明） |
| E5 | VMMap 归因 | 隐藏前后各拍一次快照，看 Private Data / Image 两栏 | 分辨那 125 MB 的组成，回答"还有没有可压空间" |

### 0.6 第三轮实测：E1 有结论了 —— 那 ~47 MB 是 **GC** 释放的，不是 EmptyWorkingSet ★★

```text
22:38:41.152 BeforeHide:          Managed=5.6MB Private=207.4MB WorkingSet=318.9MB LiveBitmapImages=72
22:38:41.444 AfterTeardown:       Managed=4.2MB Private=203.8MB WorkingSet=315.6MB LiveBitmapImages=0
22:38:46.473 AfterCompactGc:      Managed=3.9MB Private=157.1MB WorkingSet=268.9MB   ← -46.7MB 全在这里
22:38:46.497 AfterWorkingSetTrim: Managed=4.0MB Private=157.1MB WorkingSet=4.7MB      ← 对 Private 贡献 0
```

- `AfterTeardown → AfterCompactGc`：Private **−46.7 MB**，而 Managed 只降 0.3 MB → 释放的是 **native**
  （经 RCW/finalizer）。`AfterCompactGc → AfterWorkingSetTrim`：Private **±0**，只压 WS（268.9 → 4.7 MB）。
- **即 §0.5 里那条"修正"本身是错的**：当时 GC 与 trim 合并打点，才看起来像 EmptyWorkingSet 释放了 60 MB。
  真正的功臣是**延迟数秒后才执行的强制压缩 GC + `WaitForPendingFinalizers`**。

**直接推论（对隐藏路径很重要）：**

> `HideWindow` 里现有的两次 `GC.Collect()` **太早**——与 teardown 同一毫秒执行，此时刚被判离的 WinRT
> 对象还没进终结队列，所以它们几乎回收不到东西。作者注释里那句"回收后的内存占用差距非常小"正源于此。
> **真正有效的是延迟几秒后的那一轮强制 GC**（现在落在 trim 阶段）。将来若有人"优化"掉这段延迟 GC，需重新验证。

### 0.7 IdleProbe 实测（隐藏后 5 分钟，每 60s 一条）

| 轮次 | Managed | Private | WorkingSet |
| --- | --- | --- | --- |
| trim 后 | 4.0 MB | 157.1 MB | 4.7 MB |
| #1 | 4.7 MB | 157.7 MB | 35.6 MB |
| #2 | 5.5 MB | 158.9 MB | 42.2 MB |
| #3 | 6.2 MB | 159.5 MB | 43.4 MB |
| #4 | 6.9 MB | 160.2 MB | 44.5 MB |
| #5 | 7.6 MB | 161.0 MB | 48.4 MB |

判读：

1. **Managed 严格线性 +0.7 MB/min、Private +0.8 MB/min —— 但不必然是泄漏。**
   对照 dotnet-counters 的 `Allocation Rate 8.01 KiB/s` 与 **`Gen 0 GC Count 0`**：
   8 KiB/s × 60s ≈ 0.47 MB/min，与观察值同量级；因为分配太慢、gen0 预算从未填满，**GC 一次都没触发**，
   垃圾自然一直堆在 gen0。→ 属"垃圾还没被回收"，不是"对象漏了"。
   已给探针加上 `AllocTotal` / `GC=g0/g1/g2` 与逐轮 `ΔAlloc` / `ΔPrivate`，下一轮日志可直接判定。
2. **WS 4.7 → 48.4 MB**：EmptyWorkingSet 的效果本就是**暂时**的（页留在 standby，被访问即回）。
   首分钟 +31 MB，之后 +1~4 MB/min 趋于平缓。→ **"任务管理器里 11 MB"不是稳态，40~50 MB 才是隐藏态稳态。**
3. **真正待查的是"隐藏态为什么还有 8 KiB/s 分配"**（完全空闲的进程应接近 0）。候选：`FileWatcher` 的
   完成端口回调、`DispatcherQueue` 消息泵、托盘消息、WinUI 内部活动。
   **这是目前最值得挖的点**——若能把分配源压到接近 0，WS 与 Managed 都会自然稳住，也不必上周期性 trim。

**实验更新**：E1 ✅ 已完成；E2 / E3 待做；新增：

| # | 实验 | 怎么做 | 产出 |
| --- | --- | --- | --- |
| E6 | 隐藏态分配源排查 | dotnet-trace 采 alloc profile；或用"逐项屏蔽 FileWatcher / 托盘 / 探针"做对照跑 | 8 KiB/s 分配是进程固有还是可消除 |
| E7 | 周期维护（视 E6 结果） | 若分配源消不掉，再加"隐藏态每 N 分钟 GC+trim"的常量开关 | 让稳态数字持续保持低位 |

## 1. 已到头，本轮不要碰

`CacheMode`（全项目 0 处，默认即 null）｜`MainWindow` 的 `x:Bind`（XAML 里根本没有，
`MainWindow.g.cs` 连 `bindings` 类都未生成）｜`Image.Source = null` + `BitmapImage` +
`ItemsSource = null` + 延迟一帧（已比常规做法更细）｜`x:Load` 用法（Flyout/Popup 时序坑已踩过）｜
GC 顺序（已是"停交互→收浮窗→断图像→摘容器→GC×2"）｜`IgnoreImageCache`（项目既有取舍，不擅自回退）｜
`SettingsPage` 生命周期（第一阶段不动）｜`CacheLength`（与 Page 生命周期无关）。

## 2. 阶段划分（每阶段一个 commit，按 AGENTS.md 汇报"做了什么/你要测什么"）

### 阶段 0 · 诊断与探针（P0）✅ 已完成（commit eab1f47：`MemoryDiagnostics` + 探针 + 常量）

- 新增 `Infrastructure/MemoryDiagnostics.cs`：纯静态、**可开关**（Config 或 AppConstants 常量控制），
  输出 `Managed / Private Bytes / WorkingSet / LiveBitmapImageCount`。
  - 格式化函数做成**纯函数**，便于 xUnit 覆盖（本任务唯一的可单测部分）。
- 打点三处：`BeforeHide` / `AfterTeardown` / `AfterWorkingSetTrim`。
- **WeakReference 探针**（关键）：隐藏前 `_pageProbe = new WeakReference<MainPage>(CurrentMainPage)`，
  GC 后记录 `IsAlive` —— 这是唯一能硬证明"MainPage 真的死了"的手段。
- 验收：日志能完整给出三个时点的四指标 + 探针结果；关掉开关后零输出。

### 阶段 1 · 断开外部根引用（不改变页面生命周期，风险低，先做）

1. **VM → Page 委托断开/重连**（21 个，`MainPage.xaml.cs:265-332`）

   ```text
   RefreshRequested / SettingsRequested / MiniModeRequested / EditModeRequested / SelectAllRequested /
   NewCategoryRequested / CategoriesChangedRequested / ConfirmDeleteCategoryRequested /
   PromptRenameCategoryRequested / RenameCategoryFailedRequested / PromptPasteCategoryRequested /
   InvalidCategoryNameRequested / RunBatchImportRequested / EnterEditModeAndSelectRequested /
   PromptRenameMemeRequested / DeleteMemeRequested / BatchImportRequested / BatchExportRequested /
   BatchDeleteRequested / HidePreviewRequested / PasteToExternalRequested
   ```

   外加 `ViewModel.SearchDebouncer`、`ViewModel.CategorySearchDebouncer`（`:225,231`，也是 Page 的 lambda）。
   MiniPage 另有 2 个（`MiniPage.xaml.cs:49-50`：`ExpandToFullRequested` / `SendToExternalRequested`）。
   实现 `DetachViewModelCallbacks()` / `AttachViewModelCallbacks()` 成对方法，**不要**只删不连。
2. **`SetTitleBarElement(null)`**：隐藏时解除 `Window.TitleBar → TitleStrip → RootGrid → Page`。
   显示时新 Page 的 `Loaded`（`MainPage.xaml.cs:196`）会自动重注册，无需手写。
3. **teardown 前必须落盘**（spec 漏项 ⚠️）：显式 `FlushLastCategory()`。
   否则 `SaveLastCategoryDebouncer`（`:210`）随 Page 消失，**防抖未触发的最后分类选择直接丢**。
   同理检查窗口尺寸 / 分类栏宽度等 debouncer。
4. **托盘退出路径**：`Window_Closed:849` 的 `CurrentMainPage?.FlushLastCategory()` 在 Content 为 null 后
   变 no-op → 需保证退出前 flush 已发生。

### 阶段 2 · 整页卸载（核心）⚠️ 按 §0.4 实测：预期收益仅几 MB 且被噪声淹没 → 优先级下调，先做阶段 5

1. 新增 `TeardownMainPage()`（或 `ReleaseMainPageUi()`），顺序**严格**如下：

   ```text
   1. 阻止新的 UI 操作（现有 SuspendInteractions）
   2. 收 Flyout/Popup/Preview（CloseSettingsFlyout + HidePreviewPopup(immediate)）
   3. 落盘 flush（阶段 1.3）
   4. 断 Image/BitmapImage/ItemsSource（现有 ReleaseImages / ReleaseCategoryList）
   5. DetachViewModelCallbacks()
   6. SetTitleBarElement(null)                     ← 必须在 Content=null 之前
   7. 探针记录 WeakReference
   8. RootFrame.Content = null
   9. （实验项，见阶段 4）SystemBackdrop = null
   10. GC + WaitForPendingFinalizers
   ```

   `HideWindow()` 改为调用它，teardown 必须是**一个完整生命周期动作**，而不是散落补丁。
2. **`ShowWindow` 必须新增重建分支**（spec 漏项 ⚠️）：`RootFrame.Content is null` 时**同步** `Navigate`
   重建当前模式页面（**不要** `DispatcherQueue.TryEnqueue`，否则会与托盘"切换模式"
   `ToggleMode:495` 的 Show→`SwitchMode`→Navigate 抢导航）。
3. **MiniPage 一视同仁**（spec 漏项 ⚠️）：现在 `CurrentMainPage?.SetUiLoaded(false)` 对 MiniPage 是 no-op，
   Mini 模式隐藏后整棵树常驻 → MiniPage 也要走 teardown/重建。
4. **状态恢复范围（已定）**：当前分类（`MainViewModel.CurrentCategory`，单例天然保留）
   + **搜索关键词**（`SearchService.Keyword` 是单例保留的，但 `SearchBox.Text` 会随 Page 重建变空 →
   呼出时必须回填，否则出现"列表已过滤、搜索框空白"的不一致）。
   滚动位置**不恢复**（回顶部），接受此退化。
5. **`IsUiLoaded` 契约**（spec 漏项 ⚠️）：它是**单例 VM 状态**（`MainViewModel.cs:22`），跨 Page 实例残留。
   整页卸载落地后，让隐藏路径**不再依赖** x:Load（保留 x:Load 作为 Page 内的二级机制），
   避免"隐藏置 false、显示必须置 true"的隐性契约在 `SwitchMode` 等非 `ShowWindow` 路径上踩空。

### 阶段 3 · 验证与回归（不写新代码）

见 §4 测试清单。必须记录 `AfterTeardown` 三指标 + 探针 `IsAlive`。

### 阶段 4 · SystemBackdrop 实验（可回退，独立 commit）

`SystemBackdrop = null` / 恢复 `new MicaBackdrop()`。**若三指标无变化就撤回**，不保留无收益复杂度。

### 阶段 5 · 延迟 EmptyWorkingSet ✅ 已实现（commit 4519f45），待实测数据

- 时机：隐藏后**延迟**执行（避免"隐藏完马上呼出"时缺页重新调入导致可见卡顿）。
- **延迟时长写成 `AppConstants` 常量**：实际为 `AppConstants.WorkingSetTrimDelay`（实测中调成 5s；`<= TimeSpan.Zero` 表示禁用）。
- 实现建议：隐藏时启动 `Task.Delay(常量, cts.Token)`，`ShowWindow` 里 Cancel；trim 前再判一次
  `!IsAppVisible && !_isClosing`。
- 调用：`GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true)`
  → `WaitForPendingFinalizers` → `EmptyWorkingSet(Process.GetCurrentProcess().Handle)`（`psapi.dll`）。
- 认知纪律：**`EmptyWorkingSet` 只压 Working Set，不等于释放 Private Bytes，更不等于修泄漏**。

### 阶段 6 · 实验评估（可选，先别动手）

若阶段 0 的 VMMap 归因显示大头是 **Composition/D3D/框架基线**（窗口还活着就不释放），
则按 ChatGPT 第 8 节的思路评估：**"真 Close 主窗口 + 极简停车 Window 保进程存活"**。
只有它能动大头；代价是多一个 WinUI Window 生命周期对象。**先评估，不要直接实现。**

## 3. spec 中明确不做的（避免过度工程）

| 项 | 原因 |
| --- | --- |
| 统一 UI lifetime CTS / 取消所有页面任务 | 隐藏时任务已用 `IsClosing/IsAppVisible` 守卫；新造 CTS 系统收益低 |
| 给控件普遍加 `-=`（Timer 等） | `Page → Timer → Page` 是自环，GC 能处理；只优先处理"长生命周期对象持有页面" |
| `CacheLength` 调优、`SettingsPage` 生命周期重构 | 与本轮归因目标无关，会污染测量 |
| 删除 `IgnoreImageCache`、升级 Windows App SDK | 既有取舍 / 需先解决"放宽用户依赖"那条回落理由（`docs/version_history.md:128`） |
| 大面积改绑定体系、重写图片清理 | 已到头 |

## 4. 测试清单（手动回归）

- 隐藏 → 呼出（托盘 / 全局热键 / 重复启动呼出旧实例）**循环 10 次**：内存不单调上涨、无崩溃。
- 呼出后：当前分类正确、**搜索关键词回填且列表过滤一致**、标题栏可拖、min/max/close 正常。
- 隐藏态下点托盘「设置」（`MainWindow.OpenSettings:478` 依赖 `CurrentMainPage`）→ 能正常弹出。
- 隐藏态下点托盘「退出」→ 最后分类/配置已落盘（检查 `config.json`）。
- Mini 模式：隐藏 → 呼出、Mini↔Full 来回切换、Picker 浮窗、拖入导入。
- 隐藏态下从资源管理器往分类文件夹增删图片 → 呼出后列表正确（`FileWatcher` 隐藏期间仍活着）。
- 窗口显示状态下：拖拽重排、拖入文件、Ctrl+V 粘贴、悬停预览、右键菜单全部正常。
- 阶段 5 后：隐藏 → 等延迟 → 看工作集下降；再呼出测首次显示是否卡顿。

## 5. 单元测试（非 UI 部分）

- `MemoryDiagnostics` 的快照格式化/差值计算（纯函数）→ xUnit。
- `AppConstants` 新增常量的默认值/边界断言（可选）。
- UI 事件、Visual Tree、native 回收行为：以手动回归为准，不写单测（与 `MemeManager.Tests` 现有边界一致）。

## 6. 成功标准

1. **引用**：`RootFrame.Content == null`；WeakReference 探针在 GC 后 `IsAlive == false`。
2. **托管**：Managed Heap 下降量 = Page 相关包装对象（量级小，属正常，不作为主指标）。
3. **native**：记录 `BeforeHide / AfterTeardown` 的 Private Bytes 差值；剩余部分能归因到
   Image/Mapped（框架基线）还是 Private Data（我方残留）。
4. **Working Set**：`Before / After GC / After Trim` 三点对比。
5. **体验**：呼出正常、分类 + 搜索词恢复、无任何功能回归（宁可少省几 MB，不能把体验搞坏）。
