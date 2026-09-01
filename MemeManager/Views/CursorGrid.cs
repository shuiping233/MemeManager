using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Core;

namespace MemeManager.Views;

public sealed class CursorGrid : Grid
{
    public static readonly DependencyProperty CursorProperty =
        DependencyProperty.Register(
            nameof(Cursor),
            typeof(CoreCursorType),
            typeof(CursorGrid),
            new PropertyMetadata(CoreCursorType.Arrow, OnCursorChanged));

    /// <summary>
    /// 鼠标悬停时显示的光标类型，可在 XAML 中直接写，如 Cursor="Hand"
    /// </summary>
    public CoreCursorType Cursor
    {
        get => (CoreCursorType)GetValue(CursorProperty);
        set => SetValue(CursorProperty, value);
    }

    private static void OnCursorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (CursorGrid)d;
        var type = (CoreCursorType)e.NewValue;

        grid.ProtectedCursor = type == CoreCursorType.Arrow
            ? null   // Arrow = 恢复系统默认
            : InputCursor.CreateFromCoreCursor(new CoreCursor(type, 1));
    }
}
