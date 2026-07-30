## FileWatcher 分类事件（尚未实现，需在 FileWatcher.cs 中新增并分发）
- [ ] `CategoryRemoved`：分类文件夹被删除（整个分类消失）时的事件，供 MainWindow 移除左侧分类项并清理计数
- [ ] `CategoryAdded`：新建分类文件夹时的事件，供 MainWindow 在左侧分类栏追加新分类项
- [ ] `CategoryRenamed`：分类文件夹改名（重命名）时的事件，供 MainWindow 同步更新分类名（含内部顺序/metadata 关联）

## FileWatcher 文件级事件（MainWindow 已订阅/处理）
- [x] `FilesRemoved`：图片从库中消失（外部拖出/被删），移除焦点分类对应控件并刷新分类数量（`OnWatchedFilesRemoved`，MainWindow.xaml.cs:2377）
- [x] `FilesAdded`：图片新增（手动往分类文件夹塞图等兜底），追加焦点分类对应控件（`OnWatchedFilesAdded`，MainWindow.xaml.cs:2405）
- [x] `FilesMoved`：库内移动（如移动到其他分类），按焦点分类移除源控件/追加目标控件（`OnWatchedFilesMoved`，MainWindow.xaml.cs:2444）

## MVVM 架构重构
我看完之后第一反应其实不是 MVVM，而是：

> **先把工程目录整理干净，再开始 MVVM。**

因为你的 tree 里面有很多都是 VS 自动生成的东西。

真正源码其实只有这些：

```text
Assets/
Controls/
Properties/
Strings/

App.xaml
App.xaml.cs

MainWindow.xaml
MainWindow.xaml.cs

其它几个 Page
对应的 .cs
```

也就是说，你的项目目前还是一个**非常容易重构**的规模，不属于那种几十万行代码。

---

# 第一步：先理解 MVVM 后的职责

以后整个项目我建议遵循下面这个原则：

```
View（XAML）
    ↓
ViewModel（界面状态）
    ↓
Service（业务）
    ↓
Repository（数据）
    ↓
SQLite/File
```

整个项目只有这一条数据流。

---

# 我建议你的目录

我比较喜欢微软 CommunityToolkit 官方推荐风格，结合你这个项目，最后会长这样：

```
MemeManager
│
├── Assets/
│
├── Strings/
│
├── Views/
│   │
│   ├── MainWindow.xaml
│   ├── MainWindow.xaml.cs
│   │
│   ├── Pages/
│   │     HomePage.xaml
│   │     HomePage.xaml.cs
│   │
│   │     SettingsPage.xaml
│   │
│   │     MiniPage.xaml
│   │
│   └── Dialogs/
│
├── ViewModels/
│   │
│   ├── MainViewModel.cs
│   ├── HomeViewModel.cs
│   ├── SettingsViewModel.cs
│   └── MiniViewModel.cs
│
├── Models/
│   │
│   ├── MemeItem.cs
│   ├── Category.cs
│   ├── Tag.cs
│   └── Config.cs
│
├── Services/
│   │
│   ├── MemeService.cs
│   ├── CategoryService.cs
│   ├── SearchService.cs
│   ├── ImportService.cs
│   ├── ClipboardService.cs
│   ├── DragDropService.cs
│   ├── CacheService.cs
│   └── ImageService.cs
│
├── Repository/
│   │
│   ├── MemeRepository.cs
│   └── CategoryRepository.cs
│
├── Helpers/
│
├── Converters/
│
├── Behaviors/
│
├── Controls/
│
├── App.xaml
└── Program...
```

我不会再继续拆。

这是桌面程序最舒服的规模。

---

# 第二步：安装 Toolkit

NuGet：

```
CommunityToolkit.Mvvm
```

然后还有：

```
Microsoft.Extensions.DependencyInjection
```

如果以后准备 DI。

不用一次装很多。

---

# 第三步：App 改造成 DI

以后：

```csharp
public App()
{
    Services = ConfigureServices();
}
```

例如：

```csharp
services.AddSingleton<HomeViewModel>();

services.AddSingleton<HomePage>();

services.AddSingleton<MemeService>();
```

以后 Page 不再：

```
new HomePage()
```

而是：

```
App.Services.GetRequiredService<HomePage>()
```

虽然目前可以不急着改，但最好一开始就搭好。

---

# 第四步：把所有 Model 挪出来

例如：

```
ImageItem

Category

SearchResult

Config
```

这些不应该混在 Page.cs。

这一步基本不用改业务。

只是搬家。

风险最低。

---

# 第五步：开始创建第一个 ViewModel

例如：

```
HomeViewModel
```

一开始甚至只有：

```csharp
public partial class HomeViewModel
    : ObservableObject
{
}
```

然后：

HomePage：

```csharp
DataContext = new HomeViewModel();
```

先能跑。

不要一下子搬业务。

---

# 第六步：开始搬 ObservableProperty

例如：

以前：

```csharp
private bool isSearching;
```

以后：

```csharp
[ObservableProperty]
private bool isSearching;
```

Toolkit 自动生成：

```
IsSearching
```

以及：

PropertyChanged。

这一步非常爽。

---

# 第七步：开始搬 Command

例如：

原来：

```csharp
private void Delete_Click(...)
```

变：

```csharp
[RelayCommand]
private void Delete()
{
}
```

XAML：

```
Command="{Binding DeleteCommand}"
```

这是第二个收益最大的地方。

---

# 第八步：开始拆 Service

这是我觉得最关键的一步。

例如：

HomePage.cs：

现在可能有：

```
LoadImage()

Delete()

Rename()

Import()

Refresh()

Search()
```

开始分类。

例如：

```
LoadImage
Refresh
```

放：

```
ImageService
```

例如：

```
Delete

Move

Rename
```

放：

```
MemeService
```

例如：

```
CopyToClipboard

SendToQQ

DragDrop
```

放：

```
ClipboardService
```

这样 ViewModel 就只剩：

```
DeleteCommand

↓

memeService.Delete()
```

---

# 第九步：开始把 UI 事件减少

保留：

```
Loaded

SizeChanged

PointerPressed

AnimationCompleted
```

这些 UI 生命周期。

去掉：

```
Delete_Click

Import_Click

Refresh_Click

Search_Click
```

这些业务事件。

---

# 第十步：Behavior

最后再处理：

```
DragStarting

Drop

PointerReleased

SelectionChanged
```

这些比较复杂的事件。

不用第一天就弄。

---

## CommunityToolkit 到底帮你省什么？

这个很多人误会。

它不是一个 MVVM 框架。

它只是：

**自动帮你生成大量模板代码。**

例如：

以前：

```csharp
public string SearchText
{
    get
    {
    }

    set
    {
        ...
        PropertyChanged...
    }
}
```

Toolkit：

```csharp
[ObservableProperty]
private string searchText;
```

结束。

---

Command：

以前：

```csharp
public ICommand DeleteCommand;

DeleteCommand =
    new RelayCommand(Delete);
```

Toolkit：

```csharp
[RelayCommand]
private void Delete()
{

}
```

结束。

---

Messenger：

以前：

自己写事件。

Toolkit：

```csharp
WeakReferenceMessenger.Default.Send(...)
```

页面之间通信。

例如：

```
Home

↓

通知 Mini 更新

↓

不用互相引用
```

---

# 我觉得最适合你的迁移路线

千万不要：

> "今天开始，我把 MainPage.xaml.cs 全删了。"

这是 99% MVVM 重构失败的原因。

我的建议是：

```
① 安装 CommunityToolkit.Mvvm
            ↓
② 建立 ViewModels 文件夹
            ↓
③ 建立 Services 文件夹
            ↓
④ 创建 HomeViewModel（先什么都没有）
            ↓
⑤ 把所有 UI 状态迁进去
（SelectedItem、SearchText、Loading 等）
            ↓
⑥ 把 Click 一个一个改成 RelayCommand
（一次只改一个按钮）
            ↓
⑦ 把真正干活的方法搬进 Service
（复制、删除、搜索、导入）
            ↓
⑧ 最后处理 DragDrop、SelectionChanged 等复杂事件
```

整个过程中**保证项目始终能编译、能运行**。每完成一步就提交一次 Git，而不是攒一大堆改动。

---

## 结合你之前几次聊天，我还会额外建议一件事

你的项目已经有：

* 国际化（WinUI3Localizer）
* Mini 模式
* 拖拽发送
* 批量导入
* 图片缓存
* 进度条 Helper
* 后面还想加更多功能

这已经不是一个“小 Demo”了，而是一个真正的桌面应用。

所以我不会追求那种教科书式的“纯 MVVM，零 code-behind”。对于 WinUI 3，我更推荐一种**务实的 MVVM**：

* **View（XAML + 少量 code-behind）**：只负责界面、动画、生命周期、焦点管理、复杂控件交互。
* **ViewModel**：负责页面状态、命令、页面逻辑。
* **Service**：负责文件系统、数据库、缓存、剪贴板、拖拽、图片处理等业务。
* **Model**：纯数据对象。

这种结构既符合 .NET 社区的主流实践，又不会为了追求“绝对纯净”而把简单问题复杂化。对于 MemeManager 这样的项目，我认为这是维护成本和开发效率最平衡的方案。
