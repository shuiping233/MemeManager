namespace MemeManager.Models;

// 剪贴板内容类型（Ctrl+V 分流用，见 MainViewModel.GetClipboardContentKind / MainPage.Root_KeyDown）：
// 仅当剪贴板含图片/位图/文件路径类内容时才走"粘贴到分类"导入；
// 文本、HTML、RTF 等非图片内容在焦点位于搜索框时放行给 TextBox 自身粘贴，不弹提示。
public enum ClipboardContentKind
{
    /// <summary>剪贴板为空（GetContent 返回 null）</summary>
    Empty,

    /// <summary>含图片或图片路径类内容（Bitmap / StorageItems）</summary>
    Image,

    /// <summary>非图片类内容（文本、HTML、RTF 等）</summary>
    NotImage,
}
