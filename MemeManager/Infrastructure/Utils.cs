using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Windows.Foundation;

namespace MemeManager.Infrastructure;

/// <summary>
/// 与坐标 / 尺寸 / 缩放相关的纯几何计算，集中放在这里，避免散落到 XAML 事件回调里。
/// </summary>
public static class Utils
{
    /// <summary>
    /// 计算把一张 originalW x originalH 的图片塞进 maxW x maxH 框内的目标尺寸。
    /// 若原图任一边都不超过上限则返回原尺寸，否则按等比缩放到「恰好不超出」任一上限。
    /// </summary>
    public static (double width, double height) FitWithin(
        double originalWidth, double originalHeight, double maxWidth, double maxHeight)
    {
        if (originalWidth <= 0 || originalHeight <= 0)
            return (originalWidth, originalHeight);

        if (originalWidth <= maxWidth && originalHeight <= maxHeight)
            return (originalWidth, originalHeight);

        double scale = Math.Min(maxWidth / originalWidth, maxHeight / originalHeight);
        return (originalWidth * scale, originalHeight * scale);
    }

    /// <summary>
    /// 把预览 Popup 放在锚点矩形(anchor)的指定方向，并保证整体不超出屏幕(workArea)。
    /// 返回 Popup 的 (x, y) 以及实际采用的方向（DIP 坐标）。
    /// </summary>
    public static (double x, double y, Placement actual) PlacePopup(
        Rect anchor,
        double popupWidth,
        double popupHeight,
        Rect workArea,
        Placement preferredPlacement)
    {
        double above = anchor.Y - popupHeight - 8;
        double below = anchor.Bottom + 8;
        double left = anchor.X;
        double right = anchor.Right + 8;
        double overlapX = anchor.X + (anchor.Width - popupWidth) / 2;

        double x = overlapX;
        double y = below;
        var actual = Placement.Below;

        switch (preferredPlacement)
        {
            case Placement.Above:
                y = above; actual = Placement.Above;
                break;
            case Placement.Below:
                y = below; actual = Placement.Below;
                break;
            case Placement.Right:
                x = right; y = anchor.Y; actual = Placement.Right;
                break;
            case Placement.Left:
                x = anchor.X - popupWidth - 8; y = anchor.Y; actual = Placement.Left;
                break;
        }

        // 首选方向放不下则依次尝试其它方向
        if (!Fits(x, y, popupWidth, popupHeight, workArea))
        {
            if (actual == Placement.Above && Fits(overlapX, below, popupWidth, popupHeight, workArea))
            {
                x = overlapX; y = below; actual = Placement.Below;
            }
            else if (actual == Placement.Below && Fits(overlapX, above, popupWidth, popupHeight, workArea))
            {
                x = overlapX; y = above; actual = Placement.Above;
            }
            else if (actual == Placement.Right && Fits(anchor.X - popupWidth - 8, anchor.Y, popupWidth, popupHeight, workArea))
            {
                x = anchor.X - popupWidth - 8; y = anchor.Y; actual = Placement.Left;
            }
            else if (actual == Placement.Left && Fits(right, anchor.Y, popupWidth, popupHeight, workArea))
            {
                x = right; y = anchor.Y; actual = Placement.Right;
            }
        }

        // 最后兜底：硬夹进工作区，保证可见
        x = Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - popupWidth));
        y = Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - popupHeight));

        return (x, y, actual);
    }

    private static bool Fits(double x, double y, double w, double h, Rect area)
        => x >= area.X && y >= area.Y
        && x + w <= area.Right && y + h <= area.Bottom;

    private static double Clamp(double v, double min, double max)
        => v < min ? min : (v > max ? max : v);

    /// <summary>
    /// 在文件资源管理器中打开指定路径：
    /// select=true 时定位并选中该文件(/select,"路径")，否则直接打开所在文件夹。
    /// </summary>
    public static void OpenInExplorer(string path, bool select, string logTag)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = select ? $"/select,\"{path}\"" : $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"[{logTag}] 打开资源管理器失败: {ex.Message}");
        }
    }

    public static string GetInformationalVersion()
    {
        var attr = Assembly
            .GetExecutingAssembly()
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .Cast<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault();
        var v = attr?.InformationalVersion ?? string.Empty;
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    public static bool IsSystemDarkTheme()
    {
        try
        {
            if (Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") is not int or > 0)
            {
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 这个路径末尾通常会带有一个路径分隔符（例如 "\" 或 "/"）
    /// </summary>
    public static string GetExeDirectory()
    {

        return AppContext.BaseDirectory;
    }

    // ---------- 过滤视图拖拽重排（多集交换） ----------

    /// <summary>
    /// 过滤视图内的拖拽重排失败：过滤结果与拖拽后顺序不再匹配（拖拽期间数据或搜索词变化）。
    /// 调用方据此回滚容器顺序、放弃写盘，并向用户提示"重试"。
    /// </summary>
    public sealed class SubsetMismatchException(string reason) : Exception(reason);

    /// <summary>
    /// 「多集交换」：把 <paramref name="fullList"/> 中 <paramref name="filteredBefore"/> 所占的那批位置（下标），
    /// 按 <paramref name="reordered"/> 的顺序重新分配；其余项保持原位。
    /// 用于"搜索过滤后拖拽重排"——用户只能改变可见项之间的相对顺序，未显示的项无权改动。
    /// 例：full=[A,B,C,D,E,F]、filteredBefore=[B,D,E]（占位 1,3,4）、reordered=[E,B,D]
    ///     → [A,E,C,B,D,F]（C 留在原位）。
    /// 集合不一致（不是同一批元素 / 有项不在 fullList 中 / 有重复项）时抛
    /// <see cref="SubsetMismatchException"/>，由调用方回滚并提示用户重试。
    /// </summary>
    public static List<T> MergeSubsetOrder<T>(
        IReadOnlyList<T> fullList,
        IReadOnlyList<T> filteredBefore,
        IReadOnlyList<T> reordered,
        IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;

        if (filteredBefore.Count != reordered.Count)
            throw new SubsetMismatchException(
                $"过滤结果项数({filteredBefore.Count})与拖拽后项数({reordered.Count})不一致");

        // 槽位 = filteredBefore 各元素在 fullList 中的下标（filteredBefore 是 fullList 的子序列，故天然升序）
        var slots = new List<int>(filteredBefore.Count);
        foreach (var item in filteredBefore)
        {
            int idx = IndexOf(fullList, item, comparer);
            if (idx < 0)
                throw new SubsetMismatchException("过滤结果中存在已不在完整列表里的项");
            if (slots.Contains(idx))
                throw new SubsetMismatchException("过滤结果中存在重复项");
            slots.Add(idx);
        }
        slots.Sort();

        // 校验 reordered 与 filteredBefore 是同一批元素（互为排列）
        var remaining = new List<T>(filteredBefore);
        foreach (var item in reordered)
        {
            int i = IndexOf(remaining, item, comparer);
            if (i < 0)
                throw new SubsetMismatchException("拖拽后的顺序出现了过滤结果之外的项");
            remaining.RemoveAt(i);
        }
        if (remaining.Count > 0)
            throw new SubsetMismatchException("拖拽后的顺序缺少部分过滤结果项");

        var result = new List<T>(fullList);
        for (int i = 0; i < slots.Count; i++)
            result[slots[i]] = reordered[i];
        return result;
    }

    /// <summary>
    /// 把 <paramref name="target"/> 的顺序还原成 <paramref name="snapshot"/> 的顺序：
    /// 用 ObservableCollection.Move 逐项移动，避免 Clear+Add 造成容器整体重建（丢滚动位置/选中态并闪烁）。
    /// 数量或成员对不上时放弃（保持现状比部分移动更安全）。
    /// </summary>
    public static void RestoreOrder<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> snapshot,
        IEqualityComparer<T>? comparer = null)
    {
        if (target is null || snapshot is null || target.Count != snapshot.Count) return;
        comparer ??= EqualityComparer<T>.Default;

        for (int i = 0; i < snapshot.Count; i++)
        {
            if (comparer.Equals(target[i], snapshot[i])) continue;
            int cur = IndexOf(target, snapshot[i], comparer);
            if (cur < 0) return; // 快照项已不在集合里：放弃，避免部分移动造成更乱的状态
            target.Move(cur, i);
        }
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, T item, IEqualityComparer<T> comparer)
    {
        for (int i = 0; i < list.Count; i++)
            if (comparer.Equals(list[i], item)) return i;
        return -1;
    }
}

/// <summary>Popup 相对锚点的摆放方向。</summary>
public enum Placement
{
    Above,
    Below,
    Left,
    Right,
}
