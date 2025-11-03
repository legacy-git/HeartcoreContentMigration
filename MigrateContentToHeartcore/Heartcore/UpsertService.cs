using MigrateContentToHeartcore.DTOs;

namespace MigrateContentToHeartcore.Heartcore
{
	internal sealed class UpsertService
	{
		private readonly HeartcoreClient _client;
		private readonly HeartcoreOptions _opt;
		private readonly HttpClient _http;

		public UpsertService(HeartcoreClient client, HeartcoreOptions opt, HttpClient http)
		{
			_client = client;
			_opt = opt;
			_http = http;
		}

		public async Task<Guid?> UpsertAndGetKeyAsync(TVMazeShow show, IReadOnlyDictionary<string,(Guid key,string name)> existingIndex, CancellationToken ct)
		{
			// Check for duplicates FIRST
			if (existingIndex.TryGetValue(show.Id.ToString(), out var _))
			{
				return null; // Skip existing
			}
			
			var name = show.Name ?? $"Show {show.Id}";
			var summary = show.Summary ?? string.Empty;

			// Base payload
			var payload = new Dictionary<string, object>
			{
				["contentTypeAlias"] = "tVShow",
				["parentId"] = _opt.ShowsParentKey.ToString(),
				["sortOrder"] = 0,
				["showId"] = new Dictionary<string, string> { ["$invariant"] = show.Id.ToString() }
			};

			// Name
			var nameDict = new Dictionary<string, string>();
			foreach (var culture in _opt.ImportCultures) nameDict[culture] = name;
			payload["name"] = nameDict;

			// Summary
			var summaryDict = new Dictionary<string, string>();
			foreach (var culture in _opt.ImportCultures) summaryDict[culture] = summary;
			payload["showSummary"] = summaryDict;

			// Genres (tags)
			var genresTags = BuildGenresTags(show);
			if (genresTags != null)
				payload["showGenres"] = new Dictionary<string, object> { ["$invariant"] = genresTags };

			// Choose image URL based on preference (prefer medium to reduce bytes)
			string? url = _opt.ImagePreferredSize.Equals("original", StringComparison.OrdinalIgnoreCase)
				? (show.Image?.Original ?? show.Image?.Medium)
				: (show.Image?.Medium ?? show.Image?.Original);

			if (!string.IsNullOrWhiteSpace(url))
			{
				using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
				if (resp.IsSuccessStatusCode)
				{
					var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
					var fileName = Path.GetFileName(new Uri(url).AbsolutePath);

					await using var stream = await resp.Content.ReadAsStreamAsync(ct);

					// JSON must include the filename for the upload property
					payload["showImage"] = new Dictionary<string, string> { ["$invariant"] = fileName };

					var key = await _client.CreateContentWithFileAsync(
						contentPayload: payload,
						filePropertyAlias: "showImage",
						filePropertyCulture: "$invariant",
						fileName: fileName,
						contentType: contentType,
						fileStream: stream,
						ct: ct);

					return key;
				}
			}

			// Fallback when no image available
			return await _client.CreateContentAsync(payload, ct);
		}

		private string? BuildGenresTags(TVMazeShow show)
		{
			var genres = show.Genres;
			if (genres == null || genres.Length == 0) return null;
			return string.Join(",", genres);
		}
	}
}
