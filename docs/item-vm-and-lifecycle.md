# 项级操作归属与 UI 生命周期事件

## 项级操作放 MainViewModel，不放项 VM

分类操作（删/重命名/打开文件夹）不是单纯改对象——它触及 `MemeDataEngine` + 文件系统，属于页面业务，应放在 `MainViewModel`，参数用项 VM 类型：

```text
MainViewModel
    DeleteCategoryAsync(CategoryViewModel category)
    RenameCategoryAsync(CategoryViewModel category)
    OpenCategoryFolder(CategoryViewModel category)
```

不要给 `CategoryViewModel` 加 `DeleteCommand`/`RenameCommand`（不要为消 `x:DataType` 警告而给项 VM 加 Command）。同理 `MemeViewModel` 的删除/重命名等触及数据层的操作也放 `MainViewModel`，参数用项 VM 类型。

## UI 生命周期事件保留 code-behind

不要看到事件就全部迁 Command。MVVM 目标分层：

```text
用户动作 Click/Tapped → Command（迁 VM）
UI 生命周期事件      → 留 Code Behind（不迁）
```

`Opening`（常用于设置当前右键对象、动态改菜单状态、判断能否删除）、`Loaded`、`DragOver`、`Drop`、`DragItemsStarting`、`DragItemsCompleted`、`PointerMoved`、`ContainerContentChanging` 等属 UI 生命周期或拖拽视觉协商，留在 code-behind，不强行 Command 化。
