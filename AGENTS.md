# AGENTS.md

## i18n

本地化翻译文件与新增语言的流程见 [./Strings/readme.md](./Strings/readme.md)。

约定与注意事项：

- 代码中使用 `Localization.Get()` 时，key **不需要**带 `xxx.xxx` 后缀里的 `.xxx`，直接用 key 即可。例如 `Localization.Get("Settings_Title")` 而非 `Localization.Get("Settings_Title.Text")`。
- 若要在 XAML 中直接引用 i18n 文本，需给控件加 `l:Uids.Uid="xxx"`（Uid 即 resw 里的 key）。
- 若 XAML 引用的 resw 文本，需根据控件实际读取显示文本的字段，给 resw 的 type name 添加对应 `.xxx` 后缀, 举些例子：
  - `.Text` —— `TextBlock`、`MenuFlyoutItem` 等用此字段读取文本时使用。
  - `.Content` —— `Button`、`ToolTip` 等用此字段读取文本时使用。

## XAML 绑定规范（x:Bind 优先）

总体原则：

- **新代码默认优先使用 `x:Bind`**，传统 `Binding` 仅在 `x:Bind` 不适合时使用。
- `x:Bind` 是 WinUI 3 推荐方式，优势：
  1. **编译期类型检查**——绑定路径写错直接编译报错，不会像 `Binding` 那样运行期静默失效。
  2. **性能更好**——不依赖运行时反射查找。
  3. **不依赖 `DataContext` 继承**——对 `Popup` / `Flyout` / `MenuFlyout` / `ContextFlyout` 等脱离主视觉树的场景更稳定（这正是 2.7 分类右键菜单最初用 `Binding ElementName=RootGrid` 仍偶发失效的根因，`{x:Bind ViewModel.XxxCommand}` 直接解决）。

### 页面级命令/属性

推荐：

```xml
<Button Command="{x:Bind ViewModel.NewCategoryCommand}" />
<MenuFlyoutItem Command="{x:Bind ViewModel.NewCategoryCommand}" />
```

页面必须暴露**强类型** ViewModel 属性（供 `x:Bind` 编译期解析）：

```csharp
public MainViewModel ViewModel => (MainViewModel)DataContext;
```

不要写 `{x:Bind DataContext.SomeCommand}`（`DataContext` 类型是 `object`，编译期无法解析）。

### 项级菜单（DataTemplate 内）绑定页面 VM 的 Command

`DataTemplate x:DataType="CategoryViewModel"` 内，`x:Bind` 默认根是**该项 VM**（如 `CategoryViewModel`），不是页面 VM。因此**项级菜单无法直接用 `x:Bind ViewModel.XxxCommand`**（编译器会去 `CategoryViewModel` 上找 `ViewModel` 属性而报错）。这种情况**允许且应当使用 `Binding`**：

```xml
<MenuFlyoutItem
    Command="{Binding DataContext.OpenCategoryFolderCommand, ElementName=RootGrid}"
    CommandParameter="{Binding}" />
```

- `Command` 走 `RootGrid.DataContext`（= MainViewModel）拿到页面命令
- `CommandParameter="{Binding}"` 根是当前项 VM（`CategoryViewModel`），原样传参
- 对应 VM 方法：`void OpenCategoryFolder(CategoryViewModel cat)`
- 这是 `DataTemplate` 内调页面命令的**标准写法**，不是临时回退（避免写成 `x:Bind ((MainPage)App.Current.Content).ViewModel.XxxCommand` 这种又长又吓人的绝对路径）

### 不要优先使用 `Binding` 的场景

- 页面级 / 空白区域菜单（不在 `DataTemplate` 内）：一律用 `x:Bind ViewModel.SomeCommand`，不要写 `Binding DataContext.SomeCommand`（后者依赖 DataContext 与视觉树关系，遇到 `PopupRoot` / `Flyout` 容易失效）。
- **例外**：`DataTemplate` 内项级菜单要够到页面 VM 命令时，用 `Binding DataContext.XxxCommand, ElementName=RootGrid`（见上节），这是允许的。

## MVVM（CommunityToolkit.Mvvm）

- `[RelayCommand]` 生成的命令属性名规则：方法名去掉末尾的 `Async` 后缀（若有）后 + `"Command"` 后缀。XAML 绑定 `Command="{x:Bind ViewModel.XxxCommand}"` 必须与生成名严格一致，否则**编译期**即报错（用 `x:Bind` 时此问题不复存在）。
  - 例：`[RelayCommand] void OpenSettings()` → 生成 `OpenSettingsCommand`（不是 `SettingsCommand`）。
  - 例：`[RelayCommand] async Task RefreshAsync()` → 去掉 `Async` → 生成 `RefreshCommand`（不是 `RefreshAsyncCommand`）。
- `[RelayCommand]` / `[ObservableProperty]` 标注的方法/字段支持 `private`（实测 `private` 方法也能正常生成 Command 属性并绑定，无需改成 `internal`/`public`）。
- 类必须标记为 `partial`（source generator 要求）。

### 命令命名与用法规范

**命名按“用户意图”，不要按控件名**（避免 WinForms 思维）：
- ❌ `RefreshButton_Click` → `RefreshButtonClickCommand`
- ✅ `RefreshCommand`、`OpenSettingsCommand`、`DeleteSelectedMemesCommand`、`ToggleEditModeCommand`、`SelectAllCommand`

**方法参数用 VM 类型，不要 `object sender`**：
- ✅ `[RelayCommand] void DeleteMeme(MemeViewModel meme)` → XAML `CommandParameter="{Binding}"` 传入当前项
- 多参数请包装成请求类（如 `MoveMemeRequest`），不要直接多参

**返回类型**：
- `void` → 生成 `IRelayCommand`；`Task`/`Task<T>`? 注意：`Task<T>` 不支持，异步请返回 `Task`（生成 `IAsyncRelayCommand`）。需要返回值时拆成内部 `XxxInternalAsync()` 方法。
- 长耗时异步（导入/批量/扫描）可用 `[RelayCommand(IncludeCancelCommand = true)]` 生成 `XxxCommand` + `CancelXxxCommand` 支持取消。

**CanExecute（按钮自动启用/禁用）**：
- `[RelayCommand(CanExecute = nameof(CanXxx))]` + 同名 `bool CanXxx()` 方法
- 某 `[ObservableProperty]` 变化时要刷新命令可用态：`[NotifyCanExecuteChangedFor(nameof(XxxCommand))]` 标在该属性上

**其它约束**：
- 方法必须是**实例方法**，不能是 `static`（Command 需绑定 VM 实例）
- 不要手动声明与生成名相同的 `XxxCommand` 属性（会冲突）

### DataTemplate / ContextFlyout 内绑定 Page VM 的 Command（WinUI 高频坑）

> 这是 WinUI MVVM 迁移里最容易踩坑的一类：XAML 同时混合了 `ListView.ItemTemplate + x:DataType + ContextFlyout + Page ViewModel Command`。改这里务必慢，不要批量改。2.7 阶段做对，2.8（`MemeViewModel` 右键菜单）就是直接复制这个模式。

**核心认知：`DataTemplate` 内 `x:Bind` 默认根已变了，但页面 VM 仍可达**

```xml
<ListView.ItemTemplate>
    <DataTemplate x:DataType="viewmodels:CategoryViewModel">
        <Grid>
            <Grid.ContextFlyout>
                <MenuFlyout>
                    <MenuFlyoutItem Click="CategoryDelete_Click"/>
                </MenuFlyout>
            </Grid.ContextFlyout>
        </Grid>
    </DataTemplate>
</ListView.ItemTemplate>
```

这里模板内 `x:Bind` 默认根是**该项 VM**（`CategoryViewModel`），不是页面 `MainViewModel`。因此：

- ❌ `Command="{x:Bind DeleteCategoryCommand}"` → 等价于去找 `CategoryViewModel.DeleteCategoryCommand`，编译报错。
- ❌ `Command="{x:Bind ViewModel.DeleteCategoryCommand}"` → 编译器会去 `CategoryViewModel` 上找 `ViewModel` 属性，同样报错（`x:Bind` 在模板内默认根不是页面）。
- ✅ **标准写法**：用 `Binding` + `ElementName` 指向 Page 根元素（本项目根元素已命名为 `RootGrid`，**不要新增 Root/RootGrid 以外的名字**）：

```xml
<MenuFlyoutItem
    Command="{Binding DataContext.DeleteCategoryCommand, ElementName=RootGrid}"
    CommandParameter="{Binding}" />
```

- `Command` 的 `Binding` 根是 `RootGrid.DataContext`（= MainViewModel），拿到页面命令，不受 `PopupRoot`/`Flyout` 脱离视觉树影响。
- `CommandParameter="{Binding}"` 根是当前项 VM（`CategoryViewModel`），原样传参。
- 对应 VM 方法 `DeleteCategory(CategoryViewModel category)`。
- 形成：`Command` → MainViewModel 的方法；`Parameter` → 当前 CategoryViewModel。
- 这是 `DataTemplate` 内调页面命令的**标准写法**，不要为了"全用 x:Bind"而写成 `{x:Bind ((MainPage)App.Current.Content).ViewModel.XxxCommand}` 这种又长又吓人的绝对路径。

**RootGrid 已存在，不要再新增根元素**

页面 VM 用 `x:Bind ViewModel.XxxCommand` 即可解析（页面级、不在 DataTemplate 内时），不需要新增 `<Page x:Name="Root">` 或 `<Grid x:Name="Root">`。`RootGrid` 作为项级菜单 `Binding ElementName` 的锚点保留。

**不要把项级操作做成该项 VM 自己的 Command**

分类操作（删/重命名/打开文件夹）不是单纯改对象——它触及 `MemeDataEngine` + 文件系统，属于页面业务，应放在 `MainViewModel`，参数用项 VM 类型：

```text
MainViewModel
    DeleteCategory(CategoryViewModel category)
    RenameCategory(CategoryViewModel category)
    OpenCategoryFolder(CategoryViewModel category)
```

不要给 `CategoryViewModel` 加 `DeleteCommand`/`RenameCommand`（不要为消 `x:DataType` 警告而给项 VM 加 Command）。

**两个 ContextFlyout 要区分**

- `ListView.ItemTemplate` 内某项的 `ContextFlyout`：有当前项 → 用 `CommandParameter="{Binding}"` 传当前项 VM。
- `ListView.ContextFlyout`（空白区域右键菜单，无当前项）：无参数，`CommandParameter` 无意义，直接绑 `DataContext.XxxCommand, ElementName=RootGrid`，如 `AddCategoryCommand`。

**`Opening` 等 UI 生命周期事件保留 code-behind**

不要看到事件就全部迁 Command。`Opening` 常用于设置当前右键对象、动态改菜单状态、判断能否删除，属 UI 生命周期。MVVM 目标分层：

```text
用户动作 Click  → Command（迁 VM）
UI 生命周期事件 → 留 Code Behind（不迁）
```

**给 AI 的任务描述模板（迁移 CategoryList 右键菜单时直接复制）**

> 迁移 CategoryList 的 ContextFlyout 到 RelayCommand。注意：
> 1. MainPage 根元素为 RootGrid，不要新增 Root。
> 2. ListView.ItemTemplate 的 DataContext 是 CategoryViewModel，Command 必须绑定 RootGrid.DataContext 的 MainViewModel。
> 3. 当前分类对象通过 `CommandParameter="{Binding}"` 传递。
> 4. 不要给 CategoryViewModel 增加删除/重命名 Command（这类操作触及 DataEngine/文件系统，属于页面业务）。
> 5. ListView.ContextFlyout（空白区域菜单）没有参数。
> 6. 保留 MenuFlyout Opening 等 UI 生命周期事件。
