namespace MemeManager.Models;

public class CloseAppMessage
{

}

public class ResetCategorySplitterMesssage
{

}

public class CategorySplitterEnabledMessage(bool enabled)
{
    public bool Enabled { get; } = enabled;
}

// ---------- 设置页请求消息 ----------
// 方向固定为「SettingsViewModel → SettingsPage」：VM 只表达用户意图，页面负责执行依赖
// 窗口/文件选择器/XamlRoot 的副作用（与 Page→VM 的事件桥接纪律同构，只是反向）。
// 全部为单向通知、无返回值：picker 选中的路径、路径校验弹窗等结果都由页面内部消费，
// VM 不参与（所以不要改用 RequestMessage——单响应约束在多实例下会抛异常）。
// 订阅方是每次打开重建的 SettingsPage，靠 WeakReferenceMessenger 的弱引用跟踪，
// 页面被回收后不会滞留在消息总线上，因此无需在关闭时反订阅。

public class BrowseFolderRequestedMessage
{
}

public class OpenFolderRequestedMessage(string path)
{
    public string Path { get; } = path;
}

public class CloseSettingsRequestedMessage
{
}

public class AboutRequestedMessage
{
}

public class ProgramExitRequestedMessage
{
}
