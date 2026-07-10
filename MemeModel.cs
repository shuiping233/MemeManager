using System;
using System.Collections.Generic;

namespace MemeManager.Models;

public class MemeModel
{
    // 表情包的唯一哈希（作为文件名或索引键）
    public string Hash { get; set; } = string.Empty;

    // 本地文件的绝对路径
    public string LocalPath { get; set; } = string.Empty;

    // 分类名称（如：群聊装逼、黑人问号、程序员专属）
    public string Category { get; set; } = string.Empty;

    // 标签检索（用于后续做快速搜索过滤）
    public List<string> Tags { get; set; } = new();

    // 导入时间
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    // 使用频次（后续用来做热度排序）
    public int UsageCount { get; set; }
}