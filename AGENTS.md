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

- `[RelayCommand]` 生成的命令属性名 **= 方法名 + "Command" 后缀**。XAML 绑定 `Command="{Binding XxxCommand}"` 必须与生成名严格一致，否则运行期报 `property not found`（且不会编译报错，极难排查）。
  - 例：`[RelayCommand] void OpenSettings()` → 生成 `OpenSettingsCommand`（不是 `SettingsCommand`）。
  - 例：`[RelayCommand] async Task RefreshAsync()` → 生成 `RefreshCommand`。
- `[RelayCommand]` / `[ObservableProperty]` 标注的方法/字段必须是 **非 private**（`internal`/`protected`/`public` 均可）才会生成代码；`private` 不会生成，同样导致绑定找不到属性。
- 类必须标记为 `partial`（source generator 要求）。
