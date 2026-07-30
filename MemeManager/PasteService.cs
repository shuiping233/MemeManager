using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MemeManager;

public static class PasteService
{
    /// <summary>
    /// 将指定路径的图片复制到系统剪贴板（仅写入，不模拟粘贴）。
    /// 同时写入 Bitmap 与 StorageItem，使目标程序既可粘贴为图片也可粘贴为文件。
    /// </summary>
    public static async Task CopyImageToClipboardAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            // 延迟提供（SetDataProvider）：不在调用瞬间同步构造 StorageFile / Bitmap 等跨公寓
            // COM 对象，而是等目标进程真正读取剪贴板时才提供，避开 CDataPackage::GetDataHere
            // 跨公寓释放导致的 0x40080201 崩溃（dump 证实崩溃发生在其他进程读取剪贴板时）。
            // 闭包仅捕获 string 路径（值类型）。provider 回调刻意做成【同步】(见下方 lambda 无 async)：
            // 若用 await 会 Post 回 UI 线程公寓，与发送瞬间 UI 操作抢线程 → 卡顿+偶发失败；
            // 同步在系统回调线程取数可避免跨线程排队，同时保留延迟提供的不崩特性。
            var path = Path.GetFullPath(filePath);
            var package = new DataPackage();

            package.SetDataProvider(StandardDataFormats.StorageItems, (request) =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    var file = StorageFile.GetFileFromPathAsync(path).GetAwaiter().GetResult();
                    request.SetData((System.Collections.Generic.IReadOnlyList<Windows.Storage.IStorageItem>)
                        [file]);
                }
                catch (Exception ex)
                {
                    Logger.Log("复制到剪贴板(StorageItems 提供)失败: " + ex.Message);
                }
                finally
                {
                    deferral.Complete();
                }
            });

            // Bitmap 兜底：给只认 Bitmap 的老客户端（GIF 会静态化，已知代价）
            package.SetDataProvider(StandardDataFormats.Bitmap, (request) =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using (var dw = ms.GetOutputStreamAt(0))
                    using (var dwStream = dw.AsStreamForWrite())
                    {
                        dwStream.Write(bytes, 0, bytes.Length);
                        dwStream.Flush();
                    }
                    ms.Seek(0);
                    request.SetData(
                        Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(ms));
                }
                catch (Exception ex)
                {
                    Logger.Log("复制到剪贴板(Bitmap 提供)失败: " + ex.Message);
                }
                finally
                {
                    deferral.Complete();
                }
            });

            Clipboard.SetContent(package);
            // Flush 在剪贴板被其他进程占用时可能抛 COMException，这里忽略，
            // SetContent 已足够让随后的 Ctrl+V 使用数据
            try { Clipboard.Flush(); } catch { }
        }
        catch (Exception ex)
        {
            Logger.Log("================ 复制到剪贴板失败 ================");
            Logger.Log($"异常类型: {ex.GetType().FullName}");
            Logger.Log($"错误原因: {ex.Message}");
            Logger.Log($"堆栈轨迹:\n{ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Logger.Log($"内部异常: {ex.InnerException.Message}");
            }
            Logger.Log("=================================================");
        }
    }

    /// <summary>
    /// 异步将指定路径的表情包输出到当前光标所在的文本框
    /// </summary>
    /// <param name="targetWindow">可选：指定接收 Ctrl+V 的目标窗口；为空则发送到当前前台窗口</param>
    public static async Task OutputMemeToCursorAsync(string filePath, IntPtr? targetWindow = null)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            // 复用“仅复制到剪贴板”的逻辑，随后模拟 Ctrl+V 粘贴到前台窗口
            await CopyImageToClipboardAsync(filePath);

            // 数据改为延迟提供（SetDataProvider 异步构造），粘贴前多等一会确保
            // 目标进程读取时数据已就绪，避免 Ctrl+V 早于数据提供完成
            await Task.Delay(10);

            // 若未显式指定目标，则取当前前台窗口（通常是用户正在输入的应用）
            IntPtr target = targetWindow.HasValue && targetWindow.Value != IntPtr.Zero
                ? targetWindow.Value
                : NativeMethods.GetForegroundWindow();

            TriggerCtrlV(target);
        }
        catch (Exception ex)
        {
            Logger.Log("================ 表情包粘贴失败 ================");
            Logger.Log($"异常类型: {ex.GetType().FullName}");
            Logger.Log($"错误原因: {ex.Message}");
            Logger.Log($"堆栈轨迹:\n{ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Logger.Log($"内部异常: {ex.InnerException.Message}");
            }
            Logger.Log("================================================");
        }
    }

    private static void TriggerCtrlV(IntPtr? targetWindow = null)
    {
        // 若指定了目标窗口，先把前台焦点切过去，确保 Ctrl+V 落在正确的应用里
        if (targetWindow.HasValue && targetWindow.Value != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(targetWindow.Value);
            System.Threading.Thread.Sleep(20);
        }

        // 构造 4 个按键动作：Ctrl按下 -> V按下 -> V弹起 -> Ctrl弹起
        var inputs = new NativeMethods.INPUT[4];

        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].U.ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_CONTROL, dwFlags = 0 };

        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].U.ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_V, dwFlags = 0 };

        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].U.ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_V, dwFlags = NativeMethods.KEYEVENTF_KEYUP };

        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].U.ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_CONTROL, dwFlags = NativeMethods.KEYEVENTF_KEYUP };

        int size = Marshal.SizeOf<NativeMethods.INPUT>();

        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, size);

        Logger.Log($"[SendInput 调试] 预期发送: {inputs.Length}，实际成功接收: {sent}，结构体大小: {size} 字节");

        if (sent < inputs.Length)
        {
            int errorCode = Marshal.GetLastWin32Error();
            Logger.Log($"[SendInput 警告] 模拟失败！Win32 错误码: {errorCode}");
        }
    }
}
