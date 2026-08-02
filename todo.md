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
