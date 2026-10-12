# 频繁开关的重型 UI：卸载内容，别反复新建

- 反复 `new` 的 WinUI 控件/Page 不会被主动回收：`Transient` 只保证“每次解析给新实例”，回收完全交给 GC，而一次性建出来的整棵控件树会长期滞留，表现为“开关几次内存只涨不降”。所以频繁开关的重型 UI 不要靠 `Transient` 反复新建。
- 已用且验证过的做法（`MainPage`）：给 `CategoryPanel` / `MemeGridView` 这两个内容根加 `x:Load="{x:Bind ViewModel.IsUiLoaded, Mode=OneWay}"`（见 `MainPage.xaml`）：隐藏窗口时置 `false` 卸载整棵子树（分类 `ListView` 容器 + `x:Bind` 绑定对象 + `ContextFlyout` + `GridView` 容器池），再次打开置 `true` 重建。这套机制是 `x:Load` 实验验证过的。注意 `MainPage` 本身不是单例——它由 `RootFrame.Navigate` 按模式重建（`NavigationCacheMode="Disabled"`），所以 `Unloaded` 里可以且应该调 `Bindings.StopTracking()`。
- 设置浮窗走另一条路（`SettingsPage` 没有 `x:Load`）：`SettingsPage` 是 `AddSingleton`，作为 `SettingsFlyout.Content` 复用；关闭时在 `SettingsFlyout.Closed` 里把 `SettingsFlyout.Content` 置 null 断开浮窗引用（`MainPage.SettingsFlyout_Closed`），每次打开前调 `SettingsPage.OnShow()` 从 Config 重新回填全部控件值。之所以不用 `x:Load`：浮窗内容在 Page 不在视觉树上时 `x:Bind` 尚未 applied、`x:Load` 不生效（见下）。
- Flyout / Popup 内容特有的坑（已踩）：Page 不在视觉树上时 `x:Bind` 尚未 applied、`x:Load` 根本不生效——所以必须先 `ShowAt` 让 Page 进树，再置 `IsUiLoaded = true`。反过来写会永远等不到内容（浮窗一片空白）。`MainPage` 没有这个问题是因为它一直在窗口里。
- 控件赋值（含 `ItemsSource`）绝不能放在 Page 构造函数：`x:Load` 元素在 x:Bind applied 之前不存在，构造期读 `x:Name` 恒为 null。回填统一放在内容根的 `Loaded` 里，并保证幂等（`MainPage` 参考 `EnsureGridInitialized` / `UiElement_Loaded` 门控；`SettingsPage` 参考 `OnShow`）。
- 单例 Page 不要调 `Bindings.StopTracking()`：它会让 OneWay 绑定永久停更（该 API 只适用于会被真正销毁的 Page）。单例 + 内容卸载的形态下，绑定始终有效、无需也不能断开。
