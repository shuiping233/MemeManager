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
    /// <param name="targetWindow">可选：指定接收 Ctrl+V 的目标窗口；为空则发送到当前前台窗口</param>
    public static async Task OutputMemeToCursorAsync(string filePath, IntPtr? targetWindow = null)
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

            await Task.Delay(1);

            TriggerCtrlV(targetWindow);
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

    private static void TriggerCtrlV(IntPtr? targetWindow = null)
    {
        // 若指定了目标窗口，先把前台焦点切过去，确保 Ctrl+V 落在正确的应用里
        if (targetWindow.HasValue && targetWindow.Value != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(targetWindow.Value);
            System.Threading.Thread.Sleep(30);
        }

        // 1. 构造 4 个按键动作：Ctrl按下 -> V按下 -> V弹起 -> Ctrl弹起
        var inputs = new NativeMethods.INPUT[4];

        // Ctrl 按下
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].U.ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_CONTROL, dwFlags = 0 };

        // V 按下
        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].U.ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_V, dwFlags = 0 };

        // V 弹起
        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].U.ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_V, dwFlags = NativeMethods.KEYEVENTF_KEYUP };

        // Ctrl 弹起
        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].U.ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_CONTROL, dwFlags = NativeMethods.KEYEVENTF_KEYUP };

        int size = Marshal.SizeOf<NativeMethods.INPUT>();

        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, size);

        Debug.WriteLine($"[SendInput 调试] 预期发送: {inputs.Length}，实际成功接收: {sent}，结构体大小: {size} 字节");

        if (sent < inputs.Length)
        {
            int errorCode = Marshal.GetLastWin32Error();
            Debug.WriteLine($"[SendInput 警告] 模拟失败！Win32 错误码: {errorCode}");
        }
    }
}