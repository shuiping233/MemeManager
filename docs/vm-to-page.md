# VM → Page 的意图传递纪律（消息优先，勿反向依赖窗口）

VM 需要触发“依赖窗口/视觉树/XamlRoot 的副作用”（切窗口模式、弹文件选择器、关浮窗、弹对话框等）时，不能在 VM 里直接写 `App.MainWindow.Xxx()`——那会引入 VM→Window 反向依赖，破坏 MVVM 边界且让 VM 无法独立测试。VM 只表达意图，Page 负责接线。

## 首选：WeakReferenceMessenger 单向消息

适用条件：单向、无返回值、订阅方是短命 UI 对象（每次打开都重建的 Page/控件）。模板见 `SettingsViewModel`（发送）与 `SettingsPage`（订阅），消息定义在 `Models/UIMessage.cs`。

```csharp
// VM：只发意图
[RelayCommand]
private void BrowseFolder()
    => WeakReferenceMessenger.Default.Send(new BrowseFolderRequestedMessage());

// Page：构造函数里订阅一次，自己执行依赖窗口/视觉树的具体实现
WeakReferenceMessenger.Default.Register<BrowseFolderRequestedMessage>(this, (_, _) =>
{
    if (_closed) return;   // 旧实例必须静默
    _ = BrowseFolderAsync();
});
```

纪律：

- 只能在构造函数里 `Register` 一次：同一实例对同一消息重复注册会抛 `InvalidOperationException`，绝不能放进 `OnShow()` 这类每次打开都会执行的方法。
- 订阅方若是“每次打开都新建”的短命实例：必须过滤“已关闭的旧实例”——弱引用只保证“不拖住回收”，不保证“收不到消息”（旧实例在被 GC 前仍挂在总线上，会重复响应）。当前设置页是 `AddSingleton`（浮窗内容每次开合复用同一实例，关闭时只把 `SettingsFlyout.Content` 置 null），不存在此场景；将来引入 Transient Page 再照此办理。
- 短命订阅方无需反订阅（这正是它优于 `+=` 事件的地方）；页面被回收后也不会滞留在总线上。
- 不要用 `RequestMessage<T>` 表达“VM 要 UI 的返回值”：它要求单次响应，多个订阅者都 `Reply` 会抛 `InvalidOperationException`，与“可能同时存在多个 Page 实例”的现实冲突。需要结果时优先把整段逻辑下沉到 Page（picker 结果、路径回填、错误弹窗都在 Page 内部闭环）。

## 次选：VM 上的委托属性

仍在用：`MainViewModel` 的 `XxxRequested`，多为“带参数 + 明确一对一”的请求。

- 必须用 `=` 覆盖式赋值，不要用 `+=`：覆盖式天然不累积（新 Page 构造时替换旧引用），也无需 `-=` 反订阅。
- 单例 VM + 短命 Page 的累积坑（已踩并修复）：不要用 `+=` 把短命 Page 的处理器挂到单例 VM 上——每打开一次累积一份，第 N 次打开点一次按钮触发 N 次。确实需要多订阅者时才 `+=`，那时才要“具名字段保存处理器 + `Page.Unloaded` 里 `-=`”。
- 覆盖式的代价：旧 Page 会被单例 VM 持有到“下次打开”才被替换。要求“关闭即释放控件树”的页面请用卸载内容（见 `heavy-ui.md`），不要指望 `Transient` 让 GC 帮你回收。

## 例外

纯系统 API（如 `Launcher.LaunchFolderPathAsync`）不依赖窗口实例，可直接进 VM（参考 `SettingsViewModel.OpenConfigFolderCommand`），无需走消息/委托。
