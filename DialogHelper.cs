using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MemeManager;

// 统一模态弹窗 helper：所有"标题 + 描述文本"类提示，以及带冲突列表的弹窗，
// 都收敛到此处，避免各处重复 new ContentDialog 的样板，并内置常用业务文案。
public static class DialogHelper
{
    private const int ConflictLabelMaxLen = 32;

    // 基础方法：标题 + 描述文本。其余所有场景最终都走这里。
    // wrap + 可选选中，便于用户复制冲突明细。
    public static async Task ShowMessageAsync(
        XamlRoot xamlRoot,
        string title,
        string message,
        bool selectable = false)
    {
        if (xamlRoot == null) return;
        try
        {
            var content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = selectable,
            };
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "确定",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"[DialogHelper] 弹窗失败(title={title}): {ex.Message}");
        }
    }

    // 带冲突列表的弹窗：先放说明(intro)，再逐行列出明细。
    // 业务文案（如"分类xxx已存在相同图片"）由调用方通过 intro 传入，
    // 这里只负责拼接与展示。
    public static Task ShowListAsync(
        XamlRoot xamlRoot,
        string title,
        string intro,
        IEnumerable<string> lines)
    {
        var all = new List<string> { intro, "" , "明细:" };
        all.AddRange(lines);
        return ShowMessageAsync(xamlRoot, title, string.Join("\n", all), selectable: true);
    }

    // ---------- 内置常用业务文案 ----------

    // 剪贴板导入：非图片内容
    public static Task ShowNotImageAsync(XamlRoot xamlRoot) =>
        ShowMessageAsync(xamlRoot, "无法导入", "剪贴板内容不是图片，仅支持图片或图片文件。");

    // 剪贴板导入：当前分类已存在相同图片
    public static Task ShowDuplicateAsync(XamlRoot xamlRoot, string category) =>
        ShowMessageAsync(xamlRoot, "图片已存在",
            $"该图片已经在分类\"{category}\"中存在，已跳过导入。");

    // 移动图片失败：目标分类存在相同图片（hash 冲突）
    public static Task ShowMoveConflictAsync(
        XamlRoot xamlRoot,
        string targetCategory,
        IEnumerable<(string srcLabel, string dstLabel)> conflicts)
    {
        var lines = conflicts.Select(c => $"\"{c.srcLabel}\" -> \"{c.dstLabel}\"");
        return ShowListAsync(
            xamlRoot,
            "移动图片失败",
            $"分类\"{targetCategory}\"已经存在相同的图片",
            lines);
    }

    // 通用错误提示
    public static Task ShowErrorAsync(XamlRoot xamlRoot, string title, string detail) =>
        ShowMessageAsync(xamlRoot, title, detail, selectable: true);

    // 确认对话框：带"主按钮 + 取消"，返回用户选择。主按钮文案由 primaryText 指定
    // （如"删除""确定"），用于删除确认等需要二选一的场景。
    public static async Task<ContentDialogResult> ConfirmAsync(
        XamlRoot xamlRoot,
        string title,
        string message,
        string primaryText = "确定",
        string closeText = "取消")
    {
        if (xamlRoot == null) return ContentDialogResult.None;
        try
        {
            var content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryText,
                CloseButtonText = closeText,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
            };
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"[DialogHelper] 确认弹窗失败(title={title}): {ex.Message}");
            return ContentDialogResult.None;
        }
    }

    // 截断标签，避免冲突列表过长
    public static string TruncateLabel(string s)
    {
        s = string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim();
        return s.Length > ConflictLabelMaxLen ? s.Substring(0, ConflictLabelMaxLen) + "..." : s;
    }

    // 带输入框的提示弹窗：返回用户输入的文本（已 Trim）。
    // 用户点"取消"或关闭则返回 null；点"确定"即使为空/空白也返回对应字符串。
    // 确定按钮统一蓝色高亮（Primary + DefaultButton=Primary）。
    public static async Task<string?> PromptTextAsync(
        XamlRoot xamlRoot,
        string title,
        string placeholder,
        string? defaultText = null)
    {
        if (xamlRoot == null) return null;
        try
        {
            var box = new TextBox
            {
                PlaceholderText = placeholder,
                Text = defaultText ?? string.Empty,
            };
            var dialog = new ContentDialog
            {
                Title = title,
                Content = box,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                XamlRoot = xamlRoot,
                DefaultButton = ContentDialogButton.Primary
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary
                ? box.Text?.Trim()
                : null;
        }
        catch (Exception ex)
        {
            Logger.Log($"[DialogHelper] 输入弹窗失败(title={title}): {ex.Message}");
            return null;
        }
    }
}
