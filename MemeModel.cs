using System;
using System.Collections.Generic;

namespace MemeManager.Models;

public class MemeModel
{
    // 表情包的唯一哈希（不含后缀，作为文件名主体）
    public string Hash { get; set; } = string.Empty;

    // 文件后缀（含点，如 .gif）
    public string Extension { get; set; } = string.Empty;

    // 完整文件名 = Hash + Extension
    public string FileName => $"{Hash}{Extension}";

    // 本地文件的绝对路径
    public string LocalPath { get; set; } = string.Empty;

    // 分类名称（对应一级文件夹名）
    public string Category { get; set; } = string.Empty;

    // 显示标题（来自 metadata.json）
    public string Title { get; set; } = string.Empty;

    // 标签检索
    public List<string> Tags { get; set; } = new();

    // 导入时间
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    // 使用频次（后续用来做热度排序）
    public int UsageCount { get; set; }

    // 排序优先级：值越小越靠前
    public uint Priority { get; set; }
}
