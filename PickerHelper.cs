using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MemeManager;

/// <summary>
/// 封装 Avalonia 的文件/文件夹选择器（基于 StorageProvider）。
/// </summary>
public static class PickerHelper
{
    public static async Task<string?> PickFolderAsync(Window? owner = null)
    {
        if (owner?.StorageProvider == null) return null;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择表情包存放文件夹",
            AllowMultiple = false
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    public static async Task<string?> PickSingleFileAsync(
        Window? owner = null,
        params (string Name, string Extension)[] fileTypes)
    {
        var files = await PickFiles(owner, false, fileTypes);
        return files.Count > 0 ? files[0] : null;
    }

    public static async Task<IReadOnlyList<string>> PickMultipleFilesAsync(
        Window? owner = null,
        params (string Name, string Extension)[] fileTypes)
    {
        return await PickFiles(owner, true, fileTypes);
    }

    private static async Task<IReadOnlyList<string>> PickFiles(
        Window? owner, bool multiple, params (string Name, string Extension)[] fileTypes)
    {
        if (owner?.StorageProvider == null) return new List<string>();
        var options = new FilePickerOpenOptions
        {
            Title = "选择图片",
            AllowMultiple = multiple,
            FileTypeFilter = BuildFilter(fileTypes)
        };
        var files = await owner.StorageProvider.OpenFilePickerAsync(options);
        return files.Select(f => f.Path.LocalPath).ToList();
    }

    private static List<FilePickerFileType> BuildFilter((string Name, string Extension)[] fileTypes)
    {
        if (fileTypes.Length == 0)
            return new List<FilePickerFileType> { new("图片") { Patterns = new[] { "*.*" } } };

        var type = new FilePickerFileType("图片")
        {
            Patterns = fileTypes.Select(f => "*" + f.Extension).ToArray()
        };
        return new List<FilePickerFileType> { type };
    }
}
