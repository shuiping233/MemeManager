using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using MemeManager.Models;

namespace MemeManager;

public static class PasteService
{
    /// <summary>
    /// 异步将指定路径的表情包输出到当前光标所在的文本框（通过剪贴板 + 模拟 Ctrl+V）。
    /// </summary>
    public static async Task OutputMemeToCursorAsync(string filePath, IntPtr? targetWindow = null)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var win = App.MainWindow as TopLevel;
            if (win?.Clipboard != null)
            {
                var data = new DataTransfer();
                var file = await win.StorageProvider.TryGetFileFromPathAsync(filePath);
                if (file != null)
                    data.Add(DataTransferItem.CreateFile(file));
                try
                {
                    var bmp = new Bitmap(filePath);
                    data.Add(DataTransferItem.Create(DataFormat.Bitmap, bmp));
                }
                catch { }

                await win.Clipboard.SetDataAsync(data);
            }

            await Task.Delay(10);

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
        if (targetWindow.HasValue && targetWindow.Value != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(targetWindow.Value);
            System.Threading.Thread.Sleep(20);
        }

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
