using System.Text.Json.Serialization;

namespace ApiDiff.Config;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(JsonConfig))]
internal partial class JsonConfigContext : JsonSerializerContext
{
}
