using System.Collections.Generic;

namespace MemeManager.Models;

// 单个表情在 .metadata.json 中的元数据条目
public class MemeMetaEntry
{
    public string Title { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();

    // 排序优先级：值越小越靠前；导入时赋为“当前分类已有最大优先级 + 1”
    public uint Priority { get; set; } = 0;
}

// .metadata.json 的顶层结构：key=文件名(哈希+后缀), value=元数据
public class CategoryMetadata
{
    public Dictionary<string, MemeMetaEntry> Items { get; set; } = new();
}
