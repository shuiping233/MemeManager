# MemeManager 重构路线

> **总目标**：让 `MainPage.xaml.cs`（102KB）只负责页面，让 `MemeDataEngine`（~1000行）只负责数据。
>
> **原则**：已有架构演进（不是从零学 MVVM）。项目已有 `Models` / `ViewModels` / `Helpers` / `Data` 分层，
> 也有独立的 `PasteService`、`ImageDragHelper`、`ImageBatchOperationRunner`——继续朝这个方向推进即可。
> 每步保证编译通过 + 功能可运行，一步一 commit。

---

## FileWatcher 分类事件（FileWatcher.cs）

当前 `FileWatcher` 已有文件级事件（`FilesRemoved` / `FilesAdded` / `FilesMoved`，均已订阅处理），
但缺少分类层级变更的探测与分发：

- [ ] `CategoryRemoved`：分类文件夹被删除时，供 MainWindow 移除左侧分类项并清理计数
- [ ] `CategoryAdded`：新建分类文件夹时，供 MainWindow 在左侧分类栏追加新分类项
- [ ] `CategoryRenamed`：分类文件夹改名时，供 MainWindow 同步更新分类名（含内部顺序/metadata 关联）

---

## Phase 0：画图（不改代码）

把 MainPage 的所有职责区域画出来，明确哪些属于"状态"、哪些属于"业务"、哪些属于"UI 生命周期"：

- [ ] 标注 MainPage 的 UI 状态（`SearchText`、`SelectedCategory`、`SelectedMeme`、`IsSearching`、`IsLoading` 等）
- [ ] 标注 MainPage 的业务方法（搜索、导入、删除、移动、重命名、复制/粘贴/拖出发送、批量操作）
- [ ] 标注 MainPage 的 UI 生命周期（`Loaded`、`SizeChanged`、`PointerPressed`、`AnimationCompleted` 等）——这些留在 code-behind
- [ ] 标注 MainWindow.xaml.cs 的职责划分（分类栏管理、文件监听回调、窗口级状态、Mini 切换）

---

## Phase 1：搬 UI 状态到 ViewModel（不拆 Service）

**先把 MainPage 的 UI 状态搬进 `MainViewModel`，业务方法暂时不动。**
注意顺序：先搬状态，后拆 Service——否则 Service 会开始依赖 UI 状态。

- [ ] 创建 `MainViewModel`（继承 `ObservableObject`，绑定到 `MainPage.DataContext`）
- [ ] 搬 `SearchText`、`IsSearching`、`SelectedCategory`、`SelectedMeme`、`IsLoading` 等状态字段
- [ ] 在 `MemeViewModel` / `CategoryViewModel` 中把手动 `INotifyPropertyChanged` 换为 `[ObservableProperty]`（不改业务逻辑）
- [ ] 创建 `SettingsViewModel`（空壳，先绑 DataContext，后续 Phase 3 再搬业务）
- [ ] 创建 `MiniViewModel`（空壳，同上）

---

## Phase 2：按钮事件 → RelayCommand（一次一个按钮）

- [ ] 从 MainPage 最小的按钮开始（如 Refresh），改成 `[RelayCommand]`
- [ ] 逐批迁移：刷新 → 搜索 → 删除 → 导入 → 移动 → 重命名 → 复制/粘贴/发送
- [ ] XAML 同步改为 `Command="{Binding XxxCommand}"`
- [ ] 每改完一个按钮验证功能正常

---

## Phase 3：按功能拆 Service（不是按抽象层）

每拆一个功能，MainPage 减少约 200 行。顺序按依赖关系从独立到耦合：

- [ ] **搜索 → `SearchService`**：`SearchText`、搜索建议、搜索结果过滤（`MemeListStrategy` 移到这）
- [ ] **导入 → `ImportService`**：单张/批量导入、进度回调、去重判定（目前散落在 MainPage + `MemeDataEngine.ImportMemesAsync`）
- [ ] **剪贴板/发送 → `PasteService`（已有，改为实例注入）**：`CopyImageToClipboardAsync`、`OutputMemeToCursorAsync` 不再 static 调用
- [ ] **删除 & 移动 → `MemeOperationService`**：删除单张/批量、移动到其他分类（目前混在 MainPage + `MemeDataEngine`）
- [ ] **分类管理 → `CategoryService`**：新建/删除/重命名分类、分类顺序重排（目前混在 MainWindow + `MemeDataEngine`）
- [ ] **拖拽 → 保留 `ImageDragHelper`（已有）**：改为实例注入到 ViewModel，不再 static 调用
- [ ] 重命名等零散方法 → 归入对应的 Service

完成后 ViewModel 只剩：

```csharp
[RelayCommand]
private async Task Delete() => await memeOperationService.DeleteAsync(selectedMemes);
```

---

## Phase 4：拆分 MemeDataEngine（风险最高，最后做）

当前 `MemeDataEngine`（~1000行）承担了 Repository + Service + Config 三重职责：

- [ ] 从 `MemeDataEngine` 抽 `MemeRepository`：纯数据 CRUD（`GetAllMemes` / `GetMemes` / `GetCategories` / `AddCategoryAsync` / metadata 读写 / 缓存）
- [ ] 从 `MemeDataEngine` 抽配置管理：配置加载/保存（与 `AppConfig` 一起独立）
- [ ] `MemeDataEngine` 降级为 Facade：只转发调用，不自己干活
- [ ] 各 Service 改为依赖 `MemeRepository` 而非 `App.DataEngine`

---

## 最后：目录整理（Phase 0–4 全部完成后，一次性做）

分两步：先建 `MemeManager/` 子文件夹把项目嵌套进去，再在内部按功能分层。
`Helpers/` 不再保留——这些文件全是 UI 相关工作，直接归入 `Views/`。

### 目标结构

```
Repository                         ← 仓库根（.sln 在这）
│
├── MemeManager.slnx
├── README.md
├── LICENSE
├── .github/
├── docs/
├── scripts/
├── tests/
│
└── MemeManager/                   ← 项目根（.csproj 在这）
    │
    ├── MemeManager.csproj
    ├── App.xaml
    ├── App.xaml.cs
    ├── Assets/
    ├── Strings/
    ├── Properties/
    │
    ├── Views/                     ← XAML + code-behind + UI 辅助类
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
    ├── Infrastructure/            ← 横切关注点（全项目共用）
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
    ├── Behaviors/                 ← 空目录，后续拖拽/Pointer 等复杂事件放这
    │
    └── Extensions/                ← 空目录，后续扩展方法放这
```

### 执行步骤

- [ ] **Step 1**：建 `MemeManager/` 子文件夹，移入 `.csproj`、所有源码/XAML、`Assets/`、`Strings/`、`Properties/`
- [ ] **Step 2**：更新 `.sln` 中项目路径为 `MemeManager\MemeManager.csproj`，验证 `dotnet build`
- [ ] **Step 3**：在 IDE 中按上表拖入各子文件夹，IDE 自动调整命名空间（如 `MemeManager.Views`）
- [ ] **Step 4**：`dotnet build` + 完整功能回归测试
