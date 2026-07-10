using System.Collections.Generic;
using System.Text.Json.Serialization;
using MemeManager.Models;

namespace MemeManager.Data;

// AOT 安全的静态序列化元数据
[JsonSerializable(typeof(MemeModel))]
[JsonSerializable(typeof(List<MemeModel>))]
[JsonSerializable(typeof(CategoryMetadata))]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ThemeMode))]
internal partial class MemeJsonContext : JsonSerializerContext
{
}
