# AGENTS.md

## i18n

本地化翻译文件与新增语言的流程见 [./Strings/readme.md](./Strings/readme.md)。

约定与注意事项：

- 代码中使用 `Localization.Get()` 时，key **不需要**带 `xxx.xxx` 后缀里的 `.xxx`，直接用 key 即可。例如 `Localization.Get("Settings_Title")` 而非 `Localization.Get("Settings_Title.Text")`。
- 若要在 XAML 中直接引用 i18n 文本，需给控件加 `l:Uids.Uid="xxx"`（Uid 即 resw 里的 key）。
- 若 XAML 引用的 resw 文本，需根据控件实际读取显示文本的字段，给 resw 的 type name 添加对应 `.xxx` 后缀, 举些例子：
  - `.Text` —— `TextBlock`、`MenuFlyoutItem` 等用此字段读取文本时使用。
  - `.Content` —— `Button`、`ToolTip` 等用此字段读取文本时使用。

## MVVM（CommunityToolkit.Mvvm）

- `[RelayCommand]` 生成的命令属性名规则：方法名去掉末尾的 `Async` 后缀（若有）后 + `"Command"` 后缀。XAML 绑定 `Command="{Binding XxxCommand}"` 必须与生成名严格一致，否则运行期报 `property not found`（且不会编译报错，极难排查）。
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
