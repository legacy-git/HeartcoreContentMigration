using MigrateContentToHeartcore.DTOs;

namespace MigrateContentToHeartcore.Heartcore
{
	/// <summary>
	/// Encapsulates the logic to create a TV Show in Heartcore, preferring a single multipart call when an image exists.
	/// Returns the created content key, or null if the item already exists (by TVMaze showId).
	/// </summary>
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

		/// <summary>
		/// Creates a content item for a show if it doesn't already exist in the provided index.
		/// Uses a single multipart request to upload the image and create content to maximize throughput.
		/// If the image download or upload fails, falls back to creating content without the image to keep the migration moving.
		/// </summary>
		public async Task<Guid?> UpsertAndGetKeyAsync(TVMazeShow show, IReadOnlyDictionary<string, (Guid key, string name)> existingIndex, CancellationToken ct)
		{
			// Fast idempotency: skip if we've already seen this showId under the parent
			if (existingIndex.TryGetValue(show.Id.ToString(), out var _))
			{
				return null; // Skip existing
			}

			var name = show.Name ?? $"Show {show.Id}";
			var summary = show.Summary ?? string.Empty;

			// Base payload: aligns with the "TV Show" document type described in README
			var payload = new Dictionary<string, object>
			{
				["contentTypeAlias"] = "tVShow",
				["parentId"] = _opt.ShowsParentKey.ToString(),
				["sortOrder"] = 0,
				["showId"] = new Dictionary<string, string> { ["$invariant"] = show.Id.ToString() }
			};

			// Name and summary are culture-variant; we fill all requested cultures with the same value
			var nameDict = new Dictionary<string, string>();
			foreach (var culture in _opt.ImportCultures) nameDict[culture] = name;
			payload["name"] = nameDict;

			var summaryDict = new Dictionary<string, string>();
			foreach (var culture in _opt.ImportCultures) summaryDict[culture] = summary;
			payload["showSummary"] = summaryDict;

			// Genres: we store as a comma-separated tag list on invariant culture
			var genresTags = BuildGenresTags(show);
			if (genresTags != null)
				payload["showGenres"] = new Dictionary<string, object> { ["$invariant"] = genresTags };

			// Pick the image URL; prefer medium to reduce transfer size
			string? url = _opt.ImagePreferredSize.Equals("original", StringComparison.OrdinalIgnoreCase)
				? (show.Image?.Original ?? show.Image?.Medium)
				: (show.Image?.Medium ?? show.Image?.Original);

			// If an image URL exists, try to create content with file. Retry a few times on transient network errors.
			if (!string.IsNullOrWhiteSpace(url))
			{
				const int maxAttempts = 3;
				for (int attempt = 1; attempt <= maxAttempts; attempt++)
				{
					try
					{
						using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
						if (!resp.IsSuccessStatusCode)
						{
							// Non-success (404, etc.) -> don't block the migration; proceed to fallback
							break;
						}

						var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
						var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
						await using var stream = await resp.Content.ReadAsStreamAsync(ct);

						// The multipart JSON must include the filename for the upload property
						payload["showImage"] = new Dictionary<string, string> { ["$invariant"] = fileName };

						var key = await _client.CreateContentWithFileAsync(
							contentPayload: payload,
							filePropertyAlias: "showImage",
							filePropertyCulture: "$invariant",
							fileName: fileName,
							contentType: contentType,
							fileStream: stream,
							ct: ct);

						return key; // success
					}
					catch (OperationCanceledException) when (!ct.IsCancellationRequested)
					{
						// Treat HttpClient internal timeouts similarly to transient failures
						if (attempt == maxAttempts) break;
						await Task.Delay(50 * attempt, CancellationToken.None);
					}
					catch (Exception)
					{
						// Transient network failure or HTTP/2 stream reset, retry a couple times
						if (attempt == maxAttempts) break;
						await Task.Delay(50 * attempt, CancellationToken.None);
					}
				}
			}

			// Fallback when no image available or image path failed: single JSON POST
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
