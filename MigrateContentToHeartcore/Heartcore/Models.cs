using System.Text.Json.Serialization;

namespace MigrateContentToHeartcore.Heartcore
{
 // Block List value models (simplified) for Management API
 internal sealed class BlockListItem
 {
 [JsonPropertyName("contentTypeKey")] public Guid ContentTypeKey { get; set; }
 [JsonPropertyName("udi")] public string Udi { get; set; } = $"umb://element/{Guid.NewGuid()}";
 [JsonPropertyName("key")] public Guid Key { get; set; } = Guid.NewGuid();
 [JsonPropertyName("variants")] public List<BlockListVariant> Variants { get; set; } = new();
 }

 internal sealed class BlockListVariant
 {
 [JsonPropertyName("culture")] public string? Culture { get; set; }
 [JsonPropertyName("name")] public string? Name { get; set; }
 [JsonPropertyName("values")] public List<BlockListProperty> Values { get; set; } = new();
 }

 internal sealed class BlockListProperty
 {
 [JsonPropertyName("alias")] public string Alias { get; set; } = string.Empty;
 [JsonPropertyName("value")] public object? Value { get; set; }
 }
}
