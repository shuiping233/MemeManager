namespace MemeManager.Views;

// 由窗口级 Win32 拖入（WM_DROPFILES）转发的页面级落库入口。
// MainPage（完整模式）与 MiniPage（Mini 悬浮条）都需支持拖入导入，故抽成接口，
// MainWindow 只针对当前 RootFrame.Content 转发，避免强依赖具体页类型。
public interface IExternalDropPage
{
    void HandleExternalDropPaths(List<string> paths);
}
