## FileWatcher 分类事件（尚未实现，需在 FileWatcher.cs 中新增并分发）
- [ ] `CategoryRemoved`：分类文件夹被删除（整个分类消失）时的事件，供 MainWindow 移除左侧分类项并清理计数
- [ ] `CategoryAdded`：新建分类文件夹时的事件，供 MainWindow 在左侧分类栏追加新分类项
- [ ] `CategoryRenamed`：分类文件夹改名（重命名）时的事件，供 MainWindow 同步更新分类名（含内部顺序/metadata 关联）

## FileWatcher 文件级事件（MainWindow 已订阅/处理）
- [x] `FilesRemoved`：图片从库中消失（外部拖出/被删），移除焦点分类对应控件并刷新分类数量（`OnWatchedFilesRemoved`，MainWindow.xaml.cs:2377）
- [x] `FilesAdded`：图片新增（手动往分类文件夹塞图等兜底），追加焦点分类对应控件（`OnWatchedFilesAdded`，MainWindow.xaml.cs:2405）
- [x] `FilesMoved`：库内移动（如移动到其他分类），按焦点分类移除源控件/追加目标控件（`OnWatchedFilesMoved`，MainWindow.xaml.cs:2444）

## 键盘操作

- [ ] 需要修复Tab键的焦点只在搜索框和`置顶`按钮之间反复横跳的bug
- [ ] 多选模式的键盘操作支持, 虽然进入多选现在能用方向键+Enter多选图片了, 但是很多其他快捷键的配合依然不正确也操作手感差, 需要优化