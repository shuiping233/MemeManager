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
