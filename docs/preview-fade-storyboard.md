# 预览浮窗的 Storyboard 必须声明在 XAML

## 症状

鼠标悬停表情触发预览、或移出关闭预览时，进程直接退出，`crash.log` 里是：

```
FileNotFoundException:
   at System.Linq.Expressions.Compiler.AssemblyGen..ctor()
   at ... DelegateHelpers.MakeNewCustomDelegate(Type[] types)
   at WinRT.Projections.GetAbiDelegateType(Type[] typeArgs)
   at ABI.System.Collections.Generic.IListMethods`2.MakeAppendType()
   at ABI.System.Collections.Generic.IListMethods`1.Add(IObjectReference obj, T item)
   at Microsoft.UI.Xaml.Media.Animation.TimelineCollection.Add(Timeline item)
   at MemeManager.Views.MainPage.FadeInPreview()
```

## 原因

1. `Storyboard.Children` 是 WinRT 的 `TimelineCollection`（`IVector<Timeline>`）。
2. 在 C# 里 `new Storyboard()` 后调用 `Children.Add(...)`，CsWinRT 需要为 `IList<T>.Add` **动态生成 ABI 委托类型**：`Projections.GetAbiDelegateType` -> `DelegateHelpers.MakeNewCustomDelegate` -> `AssemblyGen` -> `AssemblyBuilder.DefineDynamicAssembly`。
3. 这条链依赖 `System.Reflection.Emit`，而它通常是**进程运行到该代码时才懒加载**的。如果此时运行时提供的程序集已不可用，就会抛 `FileNotFoundException`。

典型触发场景：长期驻留的进程运行期间，.NET 运行时被 Windows Update  servicing 更新（旧 patch 目录被卸载删除），进程仍在使用已映射进内存的旧运行时，之后第一次需要加载尚未加载过的程序集时就失败。重启进程即可恢复，但应用不应因此崩溃。

## 约定

- 本项目中的 `Storyboard` / `Timeline` **一律在 XAML 里声明**，代码侧只调用 `Begin()` / `Stop()`。
- 需要程序化控制时优先用 Composition 动画（`ElementCompositionPreview.GetElementVisual` + `StartAnimation("Opacity", ...)`），它不经过 WinRT 集合投影。
- 不要在 C# 里 `new Storyboard()` 再 `Children.Add(...)`，也不要为了取子动画去访问 `storyboard.Children[i]`（索引器同样会触发动态委托生成）。

预览浮窗的具体实现见 `MainPage.xaml` 的 `PreviewFadeInStoryboard` / `PreviewFadeOutStoryboard`。
