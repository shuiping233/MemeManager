using MemeManager.Infrastructure;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MemeManager.Views;

// 拖入/拖出图片的复用助手：把 MainPage 与 MiniPage 各自 copy 的繁琐片段集中到这里。
// 页面专属逻辑（如 MainPage 的编辑模式/内部重排/预览关闭等）仍留在各自 Page，不在本类。
public static class ImageDragHelper
{
    // ---------- 拖入：从 DataPackageView 收集图片路径 ----------

    // 兼容两种来源：StorageItems（文件/QQ 拖出的文件）与 Bitmap（截图/剪贴板类拖拽，先落临时文件）。
    // 返回过滤后的图片路径列表（非图片/不存在会被忽略）。
    public static async Task<List<string>> CollectDropPathsAsync(DataPackageView view)
    {
        var paths = new List<string>();

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            var items = await view.GetStorageItemsAsync();
            foreach (var item in items)
                if (item is StorageFile file && MainPage.IsImage(file.FileType))
                    paths.Add(file.Path);
        }

        if (view.Contains(StandardDataFormats.Bitmap))
        {
            try
            {
                var streamRef = await view.GetBitmapAsync();
                using var stream = await streamRef.OpenReadAsync();
                var tempPath = Path.Combine(Path.GetTempPath(), $"meme_{Guid.NewGuid():N}.png");
                using (var outStream = File.Create(tempPath))
                {
                    await stream.AsStreamForRead().CopyToAsync(outStream);
                }
                paths.Add(tempPath);
            }
            catch (Exception ex) { Logger.Log("[Drag] 拖入(Bitmap)失败: " + ex.Message); }
        }

        return paths;
    }

    // ---------- 拖出：往 DataPackage 装图片数据 ----------

    // 把一组本地图片路径装入 DataPackage，供拖到 QQ/输入框等外部目标接收。
    //  - StorageItems：延迟提供（SetDataProvider），拖放目标真正请求时才异步取 StorageFile——
    //    不在 DragItemsStarting 同步构造跨公寓 COM 对象，从而避开 DataPackage 析构时的跨公寓
    //    COM 释放竞态(0x40080201 / 0xc000027b)。paths 是本地 string 数组（值类型，无 COM 对象），
    //    DragItemsStarting 退出时没有任何跨公寓对象要释放——这正是修复关键。
    //  - 单张非 GIF 额外提供 Bitmap 兜底（给只认 Bitmap 的老客户端）；GIF 不提供 Bitmap
    //    （Bitmap 只能静态图，塞 GIF 会变第一帧，反而误导），仅作文件拖出保留动图。
    //
    // storageFileDrag：是否启用 StorageItems 文件拖出（与配置一致）。关闭时仅用 Bitmap 兜底（单张）。
    public static void ConfigureDragOut(DataPackage data, IReadOnlyList<string> paths, bool storageFileDrag)
    {
        var valid = paths
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .ToList();
        if (valid.Count == 0) return;

        if (storageFileDrag)
        {
            var arr = valid.ToArray();
            data.SetDataProvider(StandardDataFormats.StorageItems, async (request) =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    var files = await Task.WhenAll(arr.Select(p => StorageFile.GetFileFromPathAsync(p).AsTask()));
                    request.SetData(files);
                }
                catch (Exception ex) { Logger.Log("[Drag] 拖出取文件失败: " + ex.Message); }
                finally { deferral.Complete(); }
            });
        }

        // 单张非 GIF 额外 Bitmap 兜底（多张或 GIF 不提供，避免误导/额外开销）
        bool singleNonGif = valid.Count == 1 &&
            !string.Equals(Path.GetExtension(valid[0]), ".gif", StringComparison.OrdinalIgnoreCase);
        if (singleNonGif)
        {
            var singlePath = valid[0];
            if (storageFileDrag)
            {
                // 文件拖出开启：延迟提供 Bitmap（与 StorageItems  alike，避开同步构造跨公寓 COM 对象）
                data.SetDataProvider(StandardDataFormats.Bitmap, async (request) =>
                {
                    var deferral = request.GetDeferral();
                    try
                    {
                        var bytes = await Task.Run(() => File.ReadAllBytes(singlePath));
                        var ms = new InMemoryRandomAccessStream();
                        using (var dw = ms.GetOutputStreamAt(0))
                        using (var dwStream = dw.AsStreamForWrite())
                        {
                            dwStream.Write(bytes, 0, bytes.Length);
                            dwStream.Flush();
                        }
                        ms.Seek(0);
                        request.SetData(RandomAccessStreamReference.CreateFromStream(ms));
                    }
                    catch (Exception ex) { Logger.Log("[Drag] 拖出构造位图失败: " + ex.Message); }
                    finally { deferral.Complete(); }
                });
            }
            else
            {
                // 文件拖出关闭（稳定路径，与 Full 模式 MainPage 的稳定分支一致）：
                // 用【立即同步】的 SetBitmap（内存流），避免延迟提供在拖放场景下被 NTQQ 等
                // 同步拉取时取不到而表现为“空文件”。与 Full 行为完全对齐。
                try
                {
                    var bytes = File.ReadAllBytes(singlePath);
                    var ms = new InMemoryRandomAccessStream();
                    using (var dw = ms.GetOutputStreamAt(0))
                    using (var dwStream = dw.AsStreamForWrite())
                    {
                        dwStream.Write(bytes, 0, bytes.Length);
                        dwStream.Flush();
                    }
                    ms.Seek(0);
                    data.SetBitmap(RandomAccessStreamReference.CreateFromStream(ms));
                }
                catch (Exception ex) { Logger.Log("[Drag] 拖出构造位图失败: " + ex.Message); }
            }
        }

        data.RequestedOperation = DataPackageOperation.Copy;
    }

    // ---------- 导入：带“忙”守卫的统一入口 ----------

    // 导入到指定分类；若 DataEngine 正在导入（IsBusyWriting）则直接拒绝（Success=false，UI 据此忽略）。
    // 与 MainPage 的 RunBatchImportAsync 行为对齐（都受写锁保护），Mini 无进度条 UI，故不传进度。
    // 返回 (Success, Imported)：Success=false 表示被“忙”守卫拒绝；Imported 为本次新增张数。
    public static async Task<(bool Success, int Imported)> ImportPathsAsync(IEnumerable<string> paths, string category)
    {
        var list = paths.Where(p => File.Exists(p) && MainPage.IsImage(Path.GetExtension(p))).ToList();
        if (list.Count == 0) return (false, 0);

        if (App.DataEngine.IsBusyWriting)
        {
            Logger.Log("[Drag] 导入被拒：已有导入任务进行中");
            return (false, 0);
        }

        var result = await App.DataEngine.ImportMemesSafeAsync(list, category);
        Logger.Log($"[Drag] 拖入导入：新增 {result.imported}，重复 {result.duplicate}（分类={category}）");
        return (true, result.imported);
    }
}
