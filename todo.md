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
|---|---|---|---|---|---|
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
|---|---|
| ClipboardService / TrayIcon / Views 全部 | 剪贴板、托盘、拖拽、XAML——UI/系统级 |
| NativeMethods / HotKey / EcoQos | P/Invoke + 线程/进程 API |
| StartupManager | 写真实注册表（HKCU Run），测试污染系统 |
| Logger / Localization / AppConstants | 静态 + App 依赖，无逻辑 |
| CategoryService / CategoryViewModel / MiniViewModel / MemeModel / 枚举类 | 薄包装/POCO，测试价值低 |

## 前置改造（影响测试可行性）

1. `MemeDataEngine`：csproj 加 `<InternalsVisibleTo Include="MemeManager.Tests"/>` + internal 构造支持注入测试存储路径（~5 行，不改生产行为）——测试必须把库目录指到临时目录，否则会读真实 `%LOCALAPPDATA%\config.json`。
2. `ConfigService`（P2 才需要）：同理支持注入 ConfigPath（~5 行）。

## 分期

- **第一批（P0+P1）**：MemeDataEngine（含已修安全逻辑的回归锁：SanitizeCategory / IsSafeMetadataFileName / 导入扩展名校验 / 移动冲突 / 分类非法名）→ MainViewModel 命令 → Utils。约 60–80 用例，占核心价值 80%。
- **第二批（P2）**：SearchService / ConfigService / FileWatcher / LangHelper / MemeOperationService。约 20–30 用例。
- **第三批（P3）**：随缘补充，价值递减。
