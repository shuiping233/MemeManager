# AGENTS.md

项目特有约定。通用层（注释策略、`rg`/`ast-grep` 工具用法、命名与不用 emoji、祈使语气 commit、引用名字而非行号）见全局指令，此处不重复。详细背景、示例与踩坑记录见各 `docs/` 文档。

## i18n

见 `MemeManager/Strings/README.md`（English）/ `README.zh-CN.md`（中文）。核心：`Localization.Get()` 直接传资源 key，不带 `.xxx` 后缀；XAML 用 `l:Uids.Uid="xxx"`；resw type name 按控件读取字段补 `.Text`/`.Content`。

## XAML 绑定（详见 `docs/xaml-binding.md`）

- 默认 `x:Bind`；页面暴露强类型 `ViewModel` 属性（`private MainViewModel ViewModel => (MainViewModel)DataContext;`）。
- `DataTemplate` 内 `x:Bind` 根是项 VM → 页面命令用 `Binding DataContext.XxxCommand, ElementName=RootGrid` + `CommandParameter="{Binding}"`。
- 页面根锚点是 `RootGrid`，不要新增别的根名。

## RelayCommand（详见 `docs/relay-command.md`）

- 命名按用户意图非控件名；参数用 VM 类型非 `object sender`；异步返回 `Task` 非 `Task<T>`；类必须 `partial`。

## VM → Page（详见 `docs/vm-to-page.md`）

- VM 不写 `App.MainWindow.Xxx()`；用 `WeakReferenceMessenger` 单向消息，构造函数只 `Register` 一次；委托属性用 `=` 不用 `+=`。

## 项级操作与 UI 生命周期（详见 `docs/item-vm-and-lifecycle.md`）

- 删/重命名等触及 DataEngine+文件系统的命令放 `MainViewModel`，不给项 VM 加 Command。
- `Opening`/`Loaded`/`Drop` 等 UI 生命周期/拖拽事件留 code-behind；只有 Click/Tapped 迁 Command。

## 依赖注入（详见 `docs/di.md`）

- `MemeDataEngine` 进容器；`Localization`/`Logger`/`LangHelper`/`EcoQos`/`Utils`/`ImageDragHelper` 保持 static；`FileWatcher` 随 Engine 注入。

## 重型 UI（详见 `docs/heavy-ui.md`）

- 别靠 `Transient` 让 GC 回收重型 UI；`MainPage` 用 `x:Load`，`SettingsPage` 单例 + `Content=null` + `OnShow()`；Flyout 先 `ShowAt` 再 `IsUiLoaded=true`；控件赋值不放构造函数；单例 Page 别调 `Bindings.StopTracking()`。

## 不做过度工程（详见 `docs/no-over-engineering.md`）

- 不硬拆 `MemeDataEngine` 为 Repository/Facade（只抽 `ConfigService`）；拖拽逻辑不重构进 VM/Service。

## 汇报与测试（详见 `docs/reporting.md`）

- 每任务一 commit；汇报“做了什么 + 你要测什么（或『不用测』）”；非 UI 逻辑补 xUnit 到 `MemeManager.Tests/`。
