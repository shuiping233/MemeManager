namespace MemeManager.Views;

// 页面级图像资源释放契约：窗口隐藏/关闭或切换模式前，由 MainWindow 统一驱动调用，
// 让各页面断开自己持有的 Image/BitmapImage 引用（GPU 纹理随后由框架回收）。
// 实现只需“断引用”（如 ItemsSource=null、ClearImages），**不要**在此内部 GC；
// GC 由 MainWindow 在调用完 ReleaseImages 之后统一执行，避免分散 Collect。
public interface IImageReleasablePage
{
    /// <summary>
    /// 断开页面持有的图像引用。
    /// </summary>
    /// <param name="detachItemsSource">
    /// 隐藏窗口（视觉树保留）时传 true：额外把列表 ItemsSource 置空以卸载 Image 容器——
    /// 仅清 VM 字段不够，Image.Source 仍引用旧 BitmapImage，GPU 纹理不会释放（85eb33c 回归）。
    /// 切模式（旧页面视觉树即将被导航卸载）时传 false，避免在导航前扰动容器状态（曾导致切回空白）。
    /// </param>
    void ReleaseImages(bool detachItemsSource);
}
