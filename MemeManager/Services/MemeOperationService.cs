using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MemeManager.Infrastructure;
using MemeManager.Models;
using MemeManager.Views;

namespace MemeManager.Services;

// 表情写操作服务（Phase 3.4）：把"删除 / 移动 / 移动冲突守卫"的业务编排从 MainPage 抽出来。
//
// 设计约束（与 ImportExportService 一致）：
// - 后台执行 + 进度条 + UI 收尾仍委托 View 层 ImageBatchOperationRunner（持有 BatchUiContext），
//   故本服务接收 runner 实例，不重写编排。
// - 删除确认弹窗、移动冲突弹窗、写锁忙提示等 UI 行为经 IMemeOperationUi 回调甩回 Page，服务保持 UI 无关。
// - 重命名（MemeRename_Click 链路）逻辑极简（仅调 _engine.RenameMemeAsync），且由 ViewModel 命令直接驱动，
//   不依赖 runner，故**保留在 ViewModel**，不在此服务内（避免 VM 反向依赖 Page 构造的 service 实例）。
public interface IMemeOperationUi
{
    Task<bool> ConfirmDeleteMemeAsync(string title);
    Task<bool> ConfirmDeleteMemesAsync(int count);
    Task ShowMoveConflictAsync(string targetCategory, IEnumerable<(string src, string dst)> pairs);
    void OnDeleteComplete();
    Task ShowWriteBusyAsync();
}

public class MemeOperationService
{
    private readonly MemeDataEngine _engine;
    private readonly ImageBatchOperationRunner _runner;
    private readonly IMemeOperationUi _ui;

    public MemeOperationService(MemeDataEngine engine, ImageBatchOperationRunner runner, IMemeOperationUi ui)
    {
        _engine = engine;
        _runner = runner;
        _ui = ui;
    }

    public bool TryGuardWrite()
    {
        if (_runner.IsWriteActive)
        {
            _ = _ui.ShowWriteBusyAsync();
            return false;
        }
        return true;
    }

    // 单张右键删除（已含确认弹窗 + 写锁守卫）。
    public async Task DeleteMemeAsync(MemeModel model)
    {
        if (!await _ui.ConfirmDeleteMemeAsync(string.IsNullOrWhiteSpace(model.Title) ? model.FileName : model.Title))
            return;
        if (!TryGuardWrite()) return;

        var models = new[] { model };
        await _runner.RunAsync(
            BatchOperationKind.Delete,
            1,
            progress => _engine.DeleteMemesAsync(models, progress),
            affectedModels: models,
            onUiComplete: () => Logger.Log($"右键删除「{model.Title}」"));
    }

    // 批量删除选中项（已含确认弹窗 + 写锁守卫）；完成后清空网格选中态。
    public async Task DeleteMemesAsync(IReadOnlyList<MemeModel> models)
    {
        if (models.Count == 0) return;
        if (!await _ui.ConfirmDeleteMemesAsync(models.Count))
            return;
        if (!TryGuardWrite()) return;

        int total = models.Count;
        await _runner.RunAsync(
            BatchOperationKind.Delete,
            total,
            progress => _engine.DeleteMemesAsync(models, progress),
            affectedModels: models,
            onUiComplete: () =>
            {
                _ui.OnDeleteComplete();
                Logger.Log($"删除完成: {total} 个图片");
            });
    }

    // 移动一组表情到目标分类（已含写锁守卫 + 冲突守卫）。
    // 调用方负责解析"要移动哪些项"（如编辑模式下的选中项）；本方法只做执行与冲突拦截。
    public async Task MoveMemesAsync(IEnumerable<MemeModel> memes, string targetName)
    {
        var list = memes.ToList();
        if (list.Count == 0) return;
        if (!TryGuardWrite()) return;
        if (!await GuardMoveConflictAsync(list, targetName))
            return;

        int total = list.Count;
        await _runner.RunAsync(
            BatchOperationKind.Move,
            total,
            progress => _engine.MoveMemesToCategoryAsync(list, targetName, progress),
            affectedModels: list,
            onUiComplete: () => Logger.Log($"移动 {total} 张图片到分类「{targetName}」"));
    }

    // 移动前 hash 冲突守卫：若目标分类已存在相同图片则弹模态提示并阻止移动，避免同名(hash)文件被静默覆盖。
    // 返回 true 表示可继续移动。
    private async Task<bool> GuardMoveConflictAsync(IReadOnlyList<MemeModel> memes, string targetCategory)
    {
        var conflict = await _engine.FindMoveConflictAsync(memes, targetCategory);
        if (conflict == null) return true;

        var targetMemes = _engine.GetMemes(conflict).ToList();
        var conflicts = new List<(string src, string dst)>();
        foreach (var m in memes)
        {
            if (m.Category.Equals(conflict, StringComparison.OrdinalIgnoreCase)) continue;
            var dst = targetMemes.FirstOrDefault(x => x.Hash.Equals(m.Hash, StringComparison.OrdinalIgnoreCase));
            if (dst != null)
                conflicts.Add((LabelOf(m), LabelOf(dst)));
        }

        foreach (var (src, dst) in conflicts)
            Logger.Log($"[移动冲突] 阻止移动: \"{src}\" -> \"{dst}\" (目标分类=\"{conflict}\")");

        await _ui.ShowMoveConflictAsync(conflict, conflicts);
        return false;
    }

    private static string LabelOf(MemeModel m)
    {
        const int Max = 32;
        var s = string.IsNullOrWhiteSpace(m.Title) ? m.FileName : m.Title;
        return s.Length <= Max ? s : s.Substring(0, Max) + "…";
    }
}
