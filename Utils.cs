using System;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MemeManager;

/// <summary>
/// 与坐标 / 尺寸 / 缩放相关的纯几何计算，集中放在这里，避免散落到 XAML 事件回调里。
/// 所有方法都是静态的、无副作用的，方便单测与复用。
/// </summary>
public static class Utils
{
    // 预览图允许的最大尺寸（超过则等比压缩）
    public const double PreviewMaxWidth = 800;
    public const double PreviewMaxHeight = 600;

    /// <summary>
    /// 计算把一张 originalW x originalH 的图片塞进 maxW x maxH 框内的目标尺寸。
    /// 若原图任一边都不超过上限，则返回原尺寸（不放大、不缩放）。
    /// 否则按等比缩放到「恰好不超出」任一上限。
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
    /// preferredPlacement: 优先方向（上方 / 下方 / 右方 / 左方）。
    /// 返回 Popup 的 (x, y) 以及实际采用的方向。坐标均为相对屏幕（DIP）。
    /// </summary>
    public static (double x, double y, Placement actual) PlacePopup(
        Rect anchor,
        double popupWidth,
        double popupHeight,
        Rect workArea,
        Placement preferredPlacement)
    {
        // 各方向候选位置（Popup 左上角）
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

        // 边界翻转：若首选方向放不下，依次尝试其它方向。
        if (!Fits(x, y, popupWidth, popupHeight, workArea))
        {
            // 上方放不下就尝试下方，反之亦然
            if (actual == Placement.Above && Fits(overlapX, below, popupWidth, popupHeight, workArea))
            {
                x = overlapX; y = below; actual = Placement.Below;
            }
            else if (actual == Placement.Below && Fits(overlapX, above, popupWidth, popupHeight, workArea))
            {
                x = overlapX; y = above; actual = Placement.Above;
            }
            // 左右同理
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
}

/// <summary>Popup 相对锚点的摆放方向。</summary>
public enum Placement
{
    Above,
    Below,
    Left,
    Right,
}
