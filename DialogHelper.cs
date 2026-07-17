using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MemeManager;

// 统一模态弹窗 helper：所有弹窗的"标题 + 描述文本"等业务文案都集中在此处，
// 对外只暴露语义化静态方法（如 ShowMoveConflictAsync / ShowCategoryExistsAsync），
// 调用方不再出现硬编码文案，避免各处重复 new ContentDialog 的样板。
public static class DialogHelper
{
    private const int ConflictLabelMaxLen = 32;

    // 弹窗统一强制主题。默认 Default（跟随其 XamlRoot 所在可视化树）。
    // 在 App.ApplyTheme 中按配置设置：System 时解析为当前系统实际主题，
    // 避免 Win10/Win11 下弹窗主题表现不一致（Win10 默认浅色、Win11 跟随系统）。
    public static ElementTheme DialogTheme { get; set; } = ElementTheme.Default;

    // ---------- 基础方法（不直接对外暴露文案，仅内部复用） ----------

    // 标题 + 描述文本。wrap + 可选选中，便于用户复制冲突明细。
    private static async Task ShowMessageAsync(
        XamlRoot xamlRoot, string title, string message, bool selectable = false)
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
                RequestedTheme = DialogTheme,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"[DialogHelper] 弹窗失败(title={title}): {ex.Message}");
        }
    }

    // 带冲突列表的弹窗：先放说明(intro)，再逐行列出明细。
    private static Task ShowListAsync(
        XamlRoot xamlRoot, string title, string intro, IEnumerable<string> lines)
    {
        var all = new List<string> { intro, "", "明细:" };
        all.AddRange(lines);
        return ShowMessageAsync(xamlRoot, title, string.Join("\n", all), selectable: true);
    }

    // 通用错误提示：文案由调用方传入（标题也传入），用于 Settings 等外部错误。
    public static Task ShowErrorAsync(XamlRoot xamlRoot, string title, string detail) =>
        ShowMessageAsync(xamlRoot, title, detail, selectable: true);

    // 通用提示（标题 + 描述），供各业务场景自由使用。
    public static Task ShowInfoAsync(XamlRoot xamlRoot, string title, string message) =>
        ShowMessageAsync(xamlRoot, title, message);

    // 图片被拖出数据目录（拖到资源管理器等外部目标且为 Move）后提醒：
    // 受系统限制文件已被剪切走，告知用户可重新导入恢复，或按住 Ctrl 拖拽以复制。
    public static Task ShowImageMovedOutAsync(XamlRoot xamlRoot) =>
        ShowMessageAsync(xamlRoot, 
            "图片被移出",
            """
            图片文件已被移出的数据目录, 这可能是您将图片拖拽到资源管理器中导致的
            若您的目的是复制图片到目标文件夹，请按住 Ctrl 再进行拖拽
            如果需要恢复，请重新导入该图片
            """);

    // 确认对话框：带"主按钮 + 取消"，返回用户选择。主按钮文案由 primaryText 指定
    // （如"删除""确定"），用于删除确认等需要二选一的场景。
    public static async Task<ContentDialogResult> ConfirmAsync(
        XamlRoot xamlRoot, string title, string message,
        string primaryText = "确定", string closeText = "取消")
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
                RequestedTheme = DialogTheme,
            };
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"[DialogHelper] 确认弹窗失败(title={title}): {ex.Message}");
            return ContentDialogResult.None;
        }
    }

    // 带输入框的提示弹窗：返回用户输入的文本（已 Trim）。
    // 用户点"取消"或关闭则返回 null；点"确定"即使为空/空白也返回对应字符串。
    // 确定按钮统一蓝色高亮（Primary + DefaultButton=Primary）。
    public static async Task<string?> PromptTextAsync(
        XamlRoot xamlRoot, string title, string placeholder, string? defaultText = null)
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
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = DialogTheme,
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

    // 截断标签，避免冲突列表过长
    public static string TruncateLabel(string s)
    {
        s = string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim();
        return s.Length > ConflictLabelMaxLen ? s.Substring(0, ConflictLabelMaxLen) + "..." : s;
    }

    // ---------- 语义化业务弹窗（文案集中在此） ----------

    // 剪贴板导入：非图片内容
    public static Task ShowClipboardNotImageAsync(XamlRoot xamlRoot) =>
        ShowMessageAsync(xamlRoot, "无法导入", "剪贴板内容不是图片，仅支持图片或图片文件。");

    // 剪贴板导入(Ctrl+V)：当前分类已存在相同图片，展示已有冲突图片的标题
    public static Task ShowImageDuplicateAsync(XamlRoot xamlRoot, string category, string existingLabel) =>
        ShowMessageAsync(xamlRoot, "图片已存在",
            $"该图片已经在分类\"{category}\"中存在，已跳过导入。\n已有图片：{existingLabel}");

    // 移动图片失败：目标分类存在相同图片（hash 冲突）
    public static Task ShowMoveConflictAsync(
        XamlRoot xamlRoot, string targetCategory,
        IEnumerable<(string srcLabel, string dstLabel)> conflicts)
    {
        var lines = conflicts.Select(c => $"\"{c.srcLabel}\" -> \"{c.dstLabel}\"");
        return ShowListAsync(
            xamlRoot, "移动图片失败",
            $"分类\"{targetCategory}\"已经存在相同的图片", lines);
    }

    // 新建/重命名分类时，目标分类名已存在
    public static Task ShowCategoryExistsAsync(XamlRoot xamlRoot, string category) =>
        ShowMessageAsync(xamlRoot, "分类已存在",
            $"分类\"{category}\"已经存在，请换一个名称。");

    // 重命名分类失败（文件夹无法访问等）
    public static Task ShowRenameCategoryFailedAsync(XamlRoot xamlRoot) =>
        ShowMessageAsync(xamlRoot, "重命名失败",
            "分类重命名失败（可能名称已存在或文件夹无法访问）。");

    // 删除单个图片确认
    public static Task<ContentDialogResult> ConfirmDeleteMemeAsync(XamlRoot xamlRoot, string title) =>
        ConfirmAsync(xamlRoot, "删除确认", $"确定要删除「{title}」吗？", "删除");

    // 删除批量图片确认
    public static Task<ContentDialogResult> ConfirmDeleteMemesAsync(XamlRoot xamlRoot, int count) =>
        ConfirmAsync(xamlRoot, "删除确认", $"确定要删除选中的 {count} 个表情吗？", "删除");

    // 删除分类确认
    public static Task<ContentDialogResult> ConfirmDeleteCategoryAsync(XamlRoot xamlRoot, string name) =>
        ConfirmAsync(xamlRoot, "删除分类",
            $"确定要删除分类「{name}」吗？\n该分类下的所有表情与文件夹都会被删除。", "删除");

    // 新增分类输入
    public static Task<string?> PromptNewCategoryAsync(XamlRoot xamlRoot) =>
        PromptTextAsync(xamlRoot, "新增分类", "输入新分类名称");

    // 重命名分类输入（预填当前名）
    public static Task<string?> PromptRenameCategoryAsync(XamlRoot xamlRoot, string current) =>
        PromptTextAsync(xamlRoot, "重命名分类", "输入新的分类名称", current);

    // 重命名图片输入（预填当前名）
    public static Task<string?> PromptRenameMemeAsync(XamlRoot xamlRoot, string current) =>
        PromptTextAsync(xamlRoot, "重命名", "输入新的名称", current);

    // 粘贴图片到分类输入（预填当前分类）
    public static Task<string?> PromptPasteCategoryAsync(XamlRoot xamlRoot, string current) =>
        PromptTextAsync(xamlRoot, "粘贴图片到分类", "输入分类名称（不存在则新建）", current);

    // 路径不存在提示（SettingsPage 复用）
    public static Task ShowPathNotFoundAsync(XamlRoot xamlRoot, string path) =>
        ShowMessageAsync(xamlRoot, "路径不存在",
            $"指定的存放路径不存在：\n{path}\n\n已恢复为之前的有效路径。");
}
