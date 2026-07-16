using System.Collections.Generic;

namespace MemeManager.Models;

// 数据保存目录/.metadata.json 中的条目：key=分类名(文件夹名)，value=排序优先级
public class CategoryOrderEntry
{
    // 排序优先级：值越大越靠前
    public uint Priority { get; set; } = 0;
}

public class CategoryOrderMetadata
{
    public Dictionary<string, CategoryOrderEntry> Categories { get; set; } = new();
}
