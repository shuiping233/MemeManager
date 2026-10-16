using MemeManager.Models;

namespace MemeManager.Infrastructure;

// 拖出到外部进程时交付的影子副本：资源管理器在 Move 语义下会"复制到目标后删源"，交给目标的是
// %TEMP% 下的副本，数据目录因此不受影响，源侧可安全声明 Move（内置重排需要它）。
// 复制复用 MemeDataEngine.ExportMemesAsync（并行 + EcoQoS）；副本只在启动时清理——目标程序可能延迟读取。
public static class DragOutShadow
{
    // rootDir 仅测试用。
    public static void CleanupStale(string? rootDir = null)
    {
        var root = rootDir ?? AppConstants.ShadowFileTempRootPath;
        try
        {
            if (!Directory.Exists(root)) return;
            Directory.Delete(root, recursive: true);
            Logger.Log("[DragOut] 已清理上次遗留的影子副本");
        }
        catch (Exception ex)
        {
            Logger.Log("[DragOut] 清理遗留影子副本失败: " + ex.Message);
        }
    }

    // 镜像数据目录的"分类/文件名"结构；文件名保持原样，供 MemesFromDataViewAsync 按文件名反查。
    public static string PathFor(string category, string fileName, string? rootDir = null) =>
        Path.Combine(rootDir ?? AppConstants.ShadowFileTempRootPath, category, fileName);

    // 导出待拖出的图片，返回其中的副本路径。同批必属同一分类（网格只拖当前分类的项，Mini 只有单张）。
    public static async Task<IReadOnlyList<string>> ExportAsync(
        MemeDataEngine engine, IReadOnlyList<MemeModel> memes, string? rootDir = null)
    {
        if (memes.Count == 0) return Array.Empty<string>();

        var root = rootDir ?? AppConstants.ShadowFileTempRootPath;
        var category = memes[0].Category;

        // 文件名是内容哈希，同路径必同内容，已有副本直接复用。
        var pending = memes.Where(m => !File.Exists(PathFor(category, m.FileName, root)));
        if (pending.Any())
            await engine.ExportMemesAsync(pending, Path.Combine(root, category));

        var paths = new List<string>(memes.Count);
        foreach (var meme in memes)
        {
            // 缺项跳过，避免因单张复制失败整批落空。
            var copy = PathFor(category, meme.FileName, root);
            if (File.Exists(copy)) paths.Add(copy);
            else Logger.Log("[DragOut] 影子副本缺失: " + copy);
        }
        return paths;
    }
}
