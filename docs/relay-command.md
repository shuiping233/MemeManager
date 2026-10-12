# RelayCommand（CommunityToolkit.Mvvm）命名与用法

## 生成规则

- `[RelayCommand]` 生成的命令属性名 = 方法名去掉末尾 `Async` 后缀（若有）+ `Command`。XAML 绑定 `Command="{x:Bind ViewModel.XxxCommand}"` 必须与生成名严格一致，否则编译期即报错。
  - `[RelayCommand] void OpenSettings()` → `OpenSettingsCommand`（不是 `SettingsCommand`）。
  - `[RelayCommand] async Task RefreshAsync()` → 去掉 `Async` → `RefreshCommand`（不是 `RefreshAsyncCommand`）。
- `[RelayCommand]` / `[ObservableProperty]` 标注的方法/字段支持 `private`（`private` 方法也能正常生成 Command 属性并绑定，无需改成 `internal`/`public`）。
- 类必须标记为 `partial`（source generator 要求）。

## 命名与用法

- 命名按用户意图，不按控件名（避免 WinForms 思维）：`RefreshCommand`、`OpenSettingsCommand`、`DeleteSelectedMemesCommand`、`ToggleEditModeCommand`、`SelectAllCommand`，而不是 `RefreshButton_Click` → `RefreshButtonClickCommand`。
- 方法参数用 VM 类型，不要 `object sender`：`[RelayCommand] void DeleteMeme(MemeViewModel meme)` → XAML `CommandParameter="{Binding}"` 传入当前项；多参数包装成请求类（如 `MoveMemeRequest`）。
- 返回类型：`void` → `IRelayCommand`；异步返回 `Task`（生成 `IAsyncRelayCommand`）。`Task<T>` 不支持，需要返回值时拆成内部 `XxxInternalAsync()` 方法。
- 长耗时异步（导入/批量/扫描）用 `[RelayCommand(IncludeCancelCommand = true)]` 生成 `XxxCommand` + `CancelXxxCommand` 支持取消。
- 方法必须是实例方法，不能是 `static`（Command 需绑定 VM 实例）。
- 不要手动声明与生成名相同的 `XxxCommand` 属性（会冲突）。

## CanExecute（按钮自动启用/禁用）

- `[RelayCommand(CanExecute = nameof(CanXxx))]` + 同名 `bool CanXxx()` 方法。
- 某 `[ObservableProperty]` 变化时要刷新命令可用态：`[NotifyCanExecuteChangedFor(nameof(XxxCommand))]` 标在该属性上。
