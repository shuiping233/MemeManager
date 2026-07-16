using Avalonia;
using System;

namespace MemeManager
{
    internal class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            if (!App.TryAcquireSingleInstance())
                return;

            // 同步加载数据（纯 IO，早于 Avalonia 调度循环启动，不会死锁），
            // 确保 MainWindow 构造时即可读到真实分类/表情数据。
            App.DataEngine.InitializeAsync().GetAwaiter().GetResult();
            if (App.DataEngine.GetCategories().Count == 0)
                App.DataEngine.AddCategoryAsync("Default").GetAwaiter().GetResult();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
#if DEBUG
                .WithDeveloperTools()
#endif
                .WithInterFont()
                .LogToTrace();
    }
}
