using System;
using System.Linq;

namespace MigrateContentToHeartcore.Heartcore
{
	public sealed class HeartcoreOptions
	{
		public string ProjectAlias { get; init; } = string.Empty;
		public string ApiKey { get; init; } = string.Empty;
		public Guid ShowsParentKey { get; init; }
		public Guid MediaFolderKey { get; init; }
		public string[] ImportCultures { get; init; } = new[] { "en-US" };
		public bool PublishImmediately { get; init; }
		public int MaxDegreeOfParallelism { get; init; } = 16; // Increased default for performance
		public int Take { get; init; } = 0; // 0 = all shows
		public int RequestsPerSecond { get; init; } = 50; // Heartcore plan limit

		// Image handling options (for single-call multipart)
		public string ImagePreferredSize { get; init; } = "medium"; // medium | original
		public int? ImageMaxWidth { get; init; }
		public int? ImageMaxHeight { get; init; }
		public string ImageTranscodeFormat { get; init; } = "jpg"; // jpg | png
		public int ImageQuality { get; init; } = 80; // for jpg

		public static HeartcoreOptions FromEnvironment()
		{
			var projectAlias = Environment.GetEnvironmentVariable("HEARTCORE_PROJECT_ALIAS") ?? string.Empty;
			var apiKey = Environment.GetEnvironmentVariable("HEARTCORE_MANAGEMENT_API_KEY") ?? string.Empty;
			var parentKey = Environment.GetEnvironmentVariable("HEARTCORE_SHOWS_PARENT_KEY");
			var mediaFolderKey = Environment.GetEnvironmentVariable("HEARTCORE_MEDIA_FOLDER_KEY");
			var cultures = Environment.GetEnvironmentVariable("HEARTCORE_IMPORT_CULTURES");
			var publish = Environment.GetEnvironmentVariable("HEARTCORE_PUBLISH_IMMEDIATELY")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
			var degreeStr = Environment.GetEnvironmentVariable("HEARTCORE_MAX_DEGREE");
			var takeStr = Environment.GetEnvironmentVariable("HEARTCORE_TAKE");
			var rpsStr = Environment.GetEnvironmentVariable("HEARTCORE_RPS");
			
			int degree = 16;
			if (!string.IsNullOrWhiteSpace(degreeStr) && int.TryParse(degreeStr, out var parsed)) 
				degree = parsed;
			
			int take = 0;
			if (!string.IsNullOrWhiteSpace(takeStr) && int.TryParse(takeStr, out var parsedTake)) 
				take = parsedTake;

			int rps = 50;
			if (!string.IsNullOrWhiteSpace(rpsStr) && int.TryParse(rpsStr, out var parsedRps))
				rps = parsedRps;

			// Image env vars
			var imgSize = Environment.GetEnvironmentVariable("HEARTCORE_IMAGE_SIZE") ?? "medium";
			int? maxW = int.TryParse(Environment.GetEnvironmentVariable("HEARTCORE_IMAGE_MAX_WIDTH"), out var iw) ? iw : null;
			int? maxH = int.TryParse(Environment.GetEnvironmentVariable("HEARTCORE_IMAGE_MAX_HEIGHT"), out var ih) ? ih : null;
			var fmt = Environment.GetEnvironmentVariable("HEARTCORE_IMAGE_FORMAT") ?? "jpg";
			var qual = int.TryParse(Environment.GetEnvironmentVariable("HEARTCORE_IMAGE_QUALITY"), out var iq) ? iq : 80;

			return new HeartcoreOptions
			{
				ProjectAlias = projectAlias,
				ApiKey = apiKey,
				ShowsParentKey = string.IsNullOrWhiteSpace(parentKey) ? Guid.Empty : Guid.Parse(parentKey),
				MediaFolderKey = string.IsNullOrWhiteSpace(mediaFolderKey) ? Guid.Empty : Guid.Parse(mediaFolderKey),
				ImportCultures = !string.IsNullOrWhiteSpace(cultures) 
					? cultures.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) 
					: new[] { "en-US" },
				PublishImmediately = publish,
				MaxDegreeOfParallelism = degree,
				Take = take,
				RequestsPerSecond = rps,
				ImagePreferredSize = imgSize,
				ImageMaxWidth = maxW,
				ImageMaxHeight = maxH,
				ImageTranscodeFormat = fmt,
				ImageQuality = qual
			};
		}
	}
}
