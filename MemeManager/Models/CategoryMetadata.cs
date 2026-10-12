namespace MemeManager.Models;

// 单个表情在 .metadata.json 中的元数据条目
public class MemeMetaEntry
{
    public string Title { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();

    
    public uint Priority { get; set; } = 0;
}


public class CategoryMetadata
{
    public Dictionary<string, MemeMetaEntry> Items { get; set; } = new();
}
