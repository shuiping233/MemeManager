using System.Collections.Generic;
using System.Linq;
using MemeManager.Models;
using MemeManager.ViewModels;

namespace MemeManager;

/// <summary>
/// 列表构建/维护策略的抽象。把“表情/分类列表如何构建与重排”从 MainWindow 里抽离，
/// 使“复用控件”与“每次重建控件”两种模式可以互换，而不在 MainWindow 里散落 if/else。
///
/// 两种具体实现：
/// - ReuseStrategy：增量更新，复用已有 VM 与容器（切分类/刷新只换源不重建），
///   拖拽时用“锚点对齐”让被拖组对齐鼠标落点。内存常驻较高，但秒开、不抖动。
/// - RebuildStrategy：每次全量 Clear+重建 VM（旧 Image 控件随旧容器从树消失，
///   WinUI 框架会在下一帧自动释放其 GPU 纹理），隐藏后台内存能显著回落。
///
/// MainWindow 持有当前策略实例，按配置“启用控件复用策略”在两者间切换；
/// 切换后下一次 RefreshMemes/LoadCategories 自然走不同实现。
/// </summary>
public interface IMemeListStrategy
{
    /// <summary>按目标分类集合同步分类列表（Reuse=增量复用，Rebuild=整体重建）。</summary>
    void SyncCategories(ICollection<CategoryViewModel> list, IEnumerable<string> categories, System.Func<string, int> getCount);

    /// <summary>按当前表情数据刷新表情列表（Reuse=增量复用 VM，Rebuild=整体重建）。</summary>
    void RefreshMemes(ICollection<MemeViewModel> list, IEnumerable<MemeModel> memes);

    /// <summary>
    /// 拖拽完成后计算写回顺序的文件名列表。
    /// <paramref name="draggingGroup"/> 为本次被拖组的 Model（编辑模式多选时是整组）。
    /// <paramref name="anchorFileName"/> 为实际拖起的那一张文件名（Rebuild 模式忽略）。
    /// 返回 null 表示交由调用方用列表当前顺序。
    /// </summary>
    List<string>? ComputeDragOrder(IList<MemeViewModel> list, IEnumerable<MemeModel> draggingGroup, string? anchorFileName);
}
