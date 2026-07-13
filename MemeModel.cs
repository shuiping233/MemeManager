using System;
using System.Collections.Generic;

namespace MemeManager.Models;

public class MemeModel
{
    public string Hash { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public string FileName => $"{Hash}{Extension}";

    public string LocalPath { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();

    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    public int UsageCount { get; set; }

    public uint Priority { get; set; }
}
