using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace MemeManager;

/// <summary>
/// 轻量模态对话框帮助类，替代 WinUI 的 ContentDialog。
/// 基于 Avalonia Window 实现，支持消息提示、确认、文本输入三种常用形态。
/// </summary>
public static class Dialogs
{
    public static async Task ShowMessageAsync(Window? owner, string title, string content)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap });
        var ok = new Button { Content = "确定", HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dlg.Close();
        panel.Children.Add(ok);
        dlg.Content = panel;
        await dlg.ShowDialog(owner);
    }

    public static async Task<bool> ShowConfirmAsync(Window? owner, string title, string content)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };
        var result = false;
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var yes = new Button { Content = "确定" };
        var no = new Button { Content = "取消" };
        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => { result = false; dlg.Close(); };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        panel.Children.Add(buttons);
        dlg.Content = panel;
        await dlg.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// 文本输入对话框。返回用户输入的文本；用户取消则返回 null。
    /// </summary>
    public static async Task<string?> ShowInputAsync(Window? owner, string title, string? placeholder = null, string? initialText = null)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };
        var input = new TextBox
        {
            Text = initialText ?? string.Empty,
            Watermark = placeholder ?? string.Empty
        };
        var result = (string?)null;
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        panel.Children.Add(input);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var ok = new Button { Content = "确定" };
        var cancel = new Button { Content = "取消" };
        ok.Click += (_, _) => { result = input.Text; dlg.Close(); };
        cancel.Click += (_, _) => { result = null; dlg.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        dlg.Content = panel;
        await dlg.ShowDialog(owner);
        return result;
    }
}
