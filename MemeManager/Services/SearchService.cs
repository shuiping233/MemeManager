using MemeManager.Infrastructure;
using MemeManager.Models;

namespace MemeManager.Services;

// 搜索/列表查询服务：把"按分类 + 关键词查询表情"这一查询语义从 Page 抽出来，
// 统一走 DataEngine。Page 不再直接 _engine.GetMemes，而是由本服务承接，
// 为 3.4/3.5 等其它 Service 复用同一查询入口留好接缝。
//
// 注：搜索防抖（DispatcherTimer）属 UI 类型，留在 ViewModel（MainViewModel.SearchDebounceTimer）
// 与 Page 的 SearchBox_TextChanged / SearchDebounce_Tick；本服务只负责"拿到关键词后查什么"。
public class SearchService
{
    private readonly MemeDataEngine _engine;

    public SearchService(MemeDataEngine engine)
    {
        _engine = engine;
    }

    
    public string? Keyword { get; set; }

    //  - category 传 null 表示"全部表情"视图（引擎语义：返回所有分类）。
    //  - keyword 为空/空白时引擎不做过滤。
    public IReadOnlyList<MemeModel> Query(string? category, string? keyword)
    {
        var kw = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        return _engine.GetMemes(category, kw);
    }

    
    public IReadOnlyList<MemeModel> Query(string? category) => Query(category, Keyword);
}
