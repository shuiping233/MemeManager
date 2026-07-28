namespace MemeManager;

// 页面级图像资源释放契约：窗口隐藏/关闭或切换模式前，由 MainWindow 统一驱动调用，
// 让各页面断开自己持有的 Image/BitmapImage 引用（GPU 纹理随后由框架回收）。
// 实现只需“断引用”（如 ItemsSource=null、ClearImages），**不要**在此内部 GC；
// GC 由 MainWindow 在调用完 ReleaseImages 之后统一执行，避免分散 Collect。
public interface IImageReleasablePage
{
    void ReleaseImages();
}
