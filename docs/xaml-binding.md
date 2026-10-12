# XAML 绑定规范（x:Bind 优先）

## 总体原则

- 新代码默认优先使用 `x:Bind`，传统 `Binding` 仅在 `x:Bind` 不适合时使用。
- `x:Bind` 是 WinUI 3 推荐方式，优势：
  1. 编译期类型检查——绑定路径写错直接编译报错，不会像 `Binding` 那样运行期静默失效。
  2. 性能更好——不依赖运行时反射查找。
  3. 不依赖 `DataContext` 继承——对 `Popup` / `Flyout` / `MenuFlyout` / `ContextFlyout` 等脱离主视觉树的场景更稳定（分类右键菜单最初用 `Binding ElementName=RootGrid` 仍偶发失效，`{x:Bind ViewModel.XxxCommand}` 直接解决）。

## 页面级命令 / 属性

推荐：

```xml
<Button Command="{x:Bind ViewModel.NewCategoryCommand}" />
<MenuFlyoutItem Command="{x:Bind ViewModel.NewCategoryCommand}" />
```

页面必须暴露强类型 ViewModel 属性（供 `x:Bind` 编译期解析）：

```csharp
private MainViewModel ViewModel => (MainViewModel)DataContext;
```

（`x:Bind` 生成的绑定代码与本类同处一个类，`private` 即可解析，无需 `public`。）

不要写 `{x:Bind DataContext.SomeCommand}`（`DataContext` 类型是 `object`，编译期无法解析）。

页面级 / 空白区域菜单（不在 `DataTemplate` 内）一律用 `x:Bind ViewModel.SomeCommand`，不要写 `Binding DataContext.SomeCommand`（后者依赖 DataContext 与视觉树关系，遇到 `PopupRoot` / `Flyout` 容易失效）。

## 项级菜单（DataTemplate 内）绑定页面 VM 的 Command

`DataTemplate x:DataType="CategoryViewModel"` 内，`x:Bind` 默认根是该项 VM（如 `CategoryViewModel`），不是页面 VM。因此项级菜单无法直接用 `x:Bind ViewModel.XxxCommand`（编译器会去 `CategoryViewModel` 上找 `ViewModel` 属性而报错）。这种情况允许且应当使用 `Binding` + `ElementName` 指向 Page 根元素（本项目根元素已命名为 `RootGrid`，不要新增 Root/RootGrid 以外的名字）：

```xml
<MenuFlyoutItem
    Command="{Binding DataContext.OpenCategoryFolderCommand, ElementName=RootGrid}"
    CommandParameter="{Binding}" />
```

- `Command` 走 `RootGrid.DataContext`（= MainViewModel）拿到页面命令，不受 `PopupRoot`/`Flyout` 脱离视觉树影响。
- `CommandParameter="{Binding}"` 根是当前项 VM（`CategoryViewModel`），原样传参。
- 对应 VM 方法：`void OpenCategoryFolder(CategoryViewModel cat)`。
- 不要为了“全用 x:Bind”而写成 `{x:Bind ((MainPage)App.Current.Content).ViewModel.XxxCommand}` 这种绝对路径。

错误示范：

- `Command="{x:Bind DeleteCategoryCommand}"` → 等价于去找 `CategoryViewModel.DeleteCategoryCommand`，编译报错。
- `Command="{x:Bind ViewModel.DeleteCategoryCommand}"` → 编译器会去 `CategoryViewModel` 上找 `ViewModel` 属性，同样报错（`x:Bind` 在模板内默认根不是页面）。

## RootGrid 锚点

`RootGrid` 已存在，不要新增别的根元素。页面 VM 用 `x:Bind ViewModel.XxxCommand` 即可解析（页面级、不在 DataTemplate 内时），不需要新增 `<Page x:Name="Root">` 或 `<Grid x:Name="Root">`。`RootGrid` 作为项级菜单 `Binding ElementName` 的锚点保留。

## 两个 ContextFlyout 要区分

- `ListView.ItemTemplate` 内某项的 `ContextFlyout`：有当前项 → 用 `CommandParameter="{Binding}"` 传当前项 VM。
- `ListView.ContextFlyout`（空白区域右键菜单，无当前项）：无参数，`CommandParameter` 无意义，直接绑 `DataContext.XxxCommand, ElementName=RootGrid`，如 `AddCategoryCommand`。
