using System.Text.Json.Serialization;

namespace MigrateContentToHeartcore.DTOs
{
	/// <summary>
	/// Minimal shape of a TVMaze show used by this migration.
	/// Only the fields we actually need are modeled to keep deserialization light.
	/// </summary>
	public class TVMazeShow
	{
		[JsonPropertyName("id")] public int Id { get; set; }
		[JsonPropertyName("name")] public string? Name { get; set; }
		[JsonPropertyName("image")] public Image? Image { get; set; }
		[JsonPropertyName("summary")] public string? Summary { get; set; }
		[JsonPropertyName("genres")] public string[]? Genres { get; set; }
	}

	/// <summary>
	/// Nested image URLs exposed by TVMaze. We prefer the medium size for lower bandwidth.
	/// </summary>
	public class Image
	{
		[JsonPropertyName("medium")] public string? Medium { get; set; }
		[JsonPropertyName("original")] public string? Original { get; set; }
	}
}
