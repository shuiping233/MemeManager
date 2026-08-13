using MemeManager.Infrastructure;

namespace MemeManager.Services;

// 分类管理服务（Phase 3.5）：把分类的增删改 + 计数计算等"数据/业务"语义从 Page/VM 抽出来，
// 统一走 DataEngine，为上层提供干净接缝。
//
// 设计约束：
// - 分类增删改的实际 IO（建/删/改名文件夹、写 metadata）仍在 MemeDataEngine；本服务是薄封装 + 计数计算。
// - 分类列表集合（ViewModel.CategoryList）的维护、选中态、弹窗（确认/输入/已存在提示）仍留 VM/Page，
//   经事件或参数传递，不在服务内持有 UI/VM 引用。
// - LoadCategories（含选中恢复、RefreshMemes、SyncMemeDragState）强耦合 UI 与当前视图，保留在 Page，不搬。
public class CategoryService
{
    private readonly MemeDataEngine _engine;

    public CategoryService(MemeDataEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<string> GetCategories() => _engine.GetCategories();

    public Task<bool> AddCategoryAsync(string name) => _engine.AddCategoryAsync(name);

    public Task<bool> DeleteCategoryAsync(string name) => _engine.DeleteCategoryAsync(name);

    public Task<bool> RenameCategoryAsync(string oldName, string newName) =>
        _engine.RenameCategoryAsync(oldName, newName);

    // 计算各分类图片数 + "全部表情"总数（基于内存缓存，避免每分类一次 GetMemes 临时分配）。
    // 返回 (分类名→数量, 总数)；写入 VM 对象的动作由调用方完成（服务不持有 VM 引用）。
    public (Dictionary<string, int> counts, int total) ComputeCounts()
    {
        var cache = _engine.GetAllMemes();
        var counts = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var m in cache)
        {
            if (counts.TryGetValue(m.Category, out int c))
                counts[m.Category] = c + 1;
            else
                counts[m.Category] = 1;
        }
        return (counts, cache.Count);
    }
}
