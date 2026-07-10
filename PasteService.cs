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
    /// 异步将指定路径的表情包输出到当前光标所在的文本框
    /// </summary>
    public static async Task OutputMemeToCursorAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(filePath));

            var package = new DataPackage();

            System.Collections.Generic.List<Windows.Storage.IStorageItem> storageItems =
                new System.Collections.Generic.List<Windows.Storage.IStorageItem> { file };
            package.SetStorageItems(storageItems);

            var streamRef = RandomAccessStreamReference.CreateFromFile(file);
            package.SetBitmap(streamRef);

            Clipboard.SetContent(package);
            Clipboard.Flush();

            // 🎯 优化 1：给系统和剪贴板更充分的缓冲时间（从 50ms 提升至 100ms）
            // 确保前一个激活的窗口（如微信、记事本）有足够的时间重新锁定光标焦点
            await Task.Delay(100);

            // 5. 触发 Win32 SendInput 模拟按下 Ctrl + V
            TriggerCtrlV();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("================ 表情包粘贴失败 ================");
            Debug.WriteLine($"异常类型: {ex.GetType().FullName}");
            Debug.WriteLine($"错误原因: {ex.Message}");
            Debug.WriteLine($"堆栈轨迹:\n{ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Debug.WriteLine($"内部异常: {ex.InnerException.Message}");
            }
            Debug.WriteLine("================================================");
        }
    }

    private static void TriggerCtrlV()
    {
        var inputs = new NativeMethods.INPUT[4];
        int size = Marshal.SizeOf<NativeMethods.INPUT>();

        // 1. Ctrl 键按下
        inputs[0] = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_CONTROL, dwFlags = 0 }
        };

        // 2. V 键按下
        inputs[1] = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_V, dwFlags = 0 }
        };

        // 3. V 键弹起
        inputs[2] = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_V, dwFlags = NativeMethods.KEYEVENTF_KEYUP }
        };

        // 4. Ctrl 键弹起
        inputs[3] = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_CONTROL, dwFlags = NativeMethods.KEYEVENTF_KEYUP }
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, size);
    }
}