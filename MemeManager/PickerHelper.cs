using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.Gaming.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MemeManager;

/// <summary>
/// 封装 WinUI 的文件/文件夹选择器，统一处理 Win32 窗口句柄绑定与
/// Win10 兼容性（必须指定 FileTypeFilter 否则 PickSingleFolderAsync 抛 E_FAIL）。
/// </summary>
public static class PickerHelper
{
    /// <summary>
    /// 弹出文件夹选择对话框，返回用户选择的文件夹路径；用户取消则返回 null。
    /// </summary>
    public static async Task<string?> PickFolderAsync(
        Window? owner = null,
        PickerLocationId suggestedLocation = PickerLocationId.PicturesLibrary)
    {
        var picker = new FolderPicker();
        Initialize(picker, owner);
        picker.SuggestedStartLocation = suggestedLocation;
        // Win10 要求至少指定一个文件类型筛选器，否则 PickSingleFolderAsync 抛 E_FAIL
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>
    /// 弹出单文件选择对话框，返回选中文件的路径；用户取消则返回 null。
    /// </summary>
    public static async Task<string?> PickSingleFileAsync(
        Window? owner = null,
        PickerLocationId suggestedLocation = PickerLocationId.PicturesLibrary,
        params (string Name, string Extension)[] fileTypes)
    {
        var picker = new FileOpenPicker();
        Initialize(picker, owner);
        picker.SuggestedStartLocation = suggestedLocation;
        if (fileTypes.Length == 0)
            picker.FileTypeFilter.Add("*");
        else
            foreach (var (_, ext) in fileTypes)
                picker.FileTypeFilter.Add(ext);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>
    /// 弹出多文件选择对话框，返回选中文件的路径列表；用户取消则返回空列表。
    /// </summary>
    public static async Task<IReadOnlyList<string>> PickMultipleFilesAsync(
        Window? owner = null,
        PickerLocationId suggestedLocation = PickerLocationId.PicturesLibrary,
        params (string Name, string Extension)[] fileTypes)
    {
        var picker = new FileOpenPicker();
        Initialize(picker, owner);
        picker.SuggestedStartLocation = suggestedLocation;
        if (fileTypes.Length == 0)
            picker.FileTypeFilter.Add("*");
        else
            foreach (var (_, ext) in fileTypes)
                picker.FileTypeFilter.Add(ext);

        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).ToList() ?? new List<string>();
    }

    private static WindowId GetAppWindowId(Window? owner)
    {
        var window = owner ?? App.MainWindow;
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        return winId;
    }

    private static void Initialize(object picker, Window? owner)
    {
        var hWnd = owner is null
            ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)
            : WinRT.Interop.WindowNative.GetWindowHandle(owner);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
    }
}
