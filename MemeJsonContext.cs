using System.Collections.Generic;
using System.Text.Json.Serialization;
using MemeManager.Models;

namespace MemeManager.Data;

// 🎯 通过特性告诉编译器：在编译期为 MemeModel 和它的 List 集合生成纯静态的序列化/反序列化元数据
[JsonSerializable(typeof(MemeModel))]
[JsonSerializable(typeof(List<MemeModel>))]
internal partial class MemeJsonContext : JsonSerializerContext
{
    // 编译器会自动在这里生成实现，Native AOT 裁剪时绝对不会遗失该类型
}