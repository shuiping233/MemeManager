using MemeManager.Infrastructure;
using MemeManager.Models;
using MemeManager.Views;

namespace MemeManager.Services;

// 导入/导出业务服务（Phase 3.2）：把"后台批量导入 / 批量导出"的业务编排从 MainPage 抽出来。
//
// 设计约束（与本项目 MVVM 纪律一致）：
// - 实际后台执行 + 进度条 + UI 线程收尾仍由 View 层的 ImageBatchOperationRunner 负责
//   （它持有 BatchUiContext，依赖 DispatcherQueue/XamlRoot 等 UI 能力），故本服务**接收 runner 实例**，
//   而不是自己重写一遍编排。
// - 仅"写操作"专属的弹窗（写锁忙提示、单张重复提示）属 UI，通过 IImportExportUi 回调甩回 Page，
//   服务本身不引用 XamlRoot / DialogHelper，保持 UI 无关。
// - 导入时新建分类需回填 ViewModel.CategoryList，由调用方通过 onCategoryCreated 回调注入（不耦合具体 VM）。
public interface IImportExportUi
{
    Task ShowWriteBusyAsync();
    Task ShowSingleImportDuplicateAsync(MemeModel duplicate);
}

public class ImportExportService
{
    private readonly MemeDataEngine _engine;
    private readonly ImageBatchOperationRunner _runner;
    private readonly IImportExportUi _ui;

    public ImportExportService(MemeDataEngine engine, ImageBatchOperationRunner runner, IImportExportUi ui)
    {
        _engine = engine;
        _runner = runner;
        _ui = ui;
    }

    // 写操作入口守卫：若已有用户主动写任务在跑，弹提示并拒绝。返回 false 表示被拒。
    // 与 MainPage 原 TryGuardWrite 行为一致（runner.IsWriteActive 是写锁真相源）。
    public bool TryGuardWrite()
    {
        if (_runner.IsWriteActive)
        {
            _ = _ui.ShowWriteBusyAsync();
            return false;
        }
        return true;
    }

    // 后台批量导入：复用 runner 的 Import 分支（进度条 + 分类守卫刷新 + 写锁占用）。
    // onCategoryCreated：导入过程中真正新建了分类目录时回调（UI 据此把新分类加入左侧栏）。
    public async Task RunBatchImportAsync(
        IEnumerable<string> files,
        string category,
        Action<string>? onCategoryCreated = null)
    {
        if (!TryGuardWrite()) return;

        var list = files.ToList();
        int total = list.Count;
        if (total == 0) return;

        string targetCategory = category;

        (int imported, int duplicate, MemeModel? duplicateModel) result = default;

        await _runner.RunAsync(
            BatchOperationKind.Import,
            total,
            work: async progress =>
            {
                result = await _engine.ImportMemesAsync(list, targetCategory, progress,
                    onCategoryCreated: createdName => onCategoryCreated?.Invoke(createdName));
            },
            targetCategory: targetCategory,
            onUiComplete: () =>
            {
                Logger.Log($"导入完成: 新增 {result.imported} 个, 重复跳过 {result.duplicate} 个");
                if (list.Count == 1 && result.duplicateModel != null)
                    _ = _ui.ShowSingleImportDuplicateAsync(result.duplicateModel);
            });
    }

    // 批量导出：copy 语义，不占用写入锁（不改缓存/源文件）。
    public async Task BatchExportCoreAsync(IEnumerable<MemeModel> models, string folder)
    {
        var list = models.ToList();
        int total = list.Count;
        if (total == 0) return;

        await _runner.RunAsync(
            BatchOperationKind.Export,
            total,
            progress => _engine.ExportMemesAsync(list, folder, progress),
            onUiComplete: () => Logger.Log($"导出完成: {total} 个图片到 {folder}"));
    }
}
