using MigrateContentToHeartcore.DTOs;
using System.Net;

namespace MigrateContentToHeartcore.Heartcore
{
 internal sealed class UpsertService
 {
 private readonly HeartcoreClient _client;
 private readonly HeartcoreOptions _opt;
 private readonly HttpClient _http;
 private readonly Translator? _translator;

 public UpsertService(HeartcoreClient client, HeartcoreOptions opt, HttpClient http, Translator? translator = null)
 {
 _client = client;
 _opt = opt;
 _http = http;
 _translator = translator;
 }

 public async Task<bool> UpsertAsync(TVMazeShow show, Dictionary<string,(Guid key,string name)> existingIndex, CancellationToken ct)
 {
 // Build or locate image UDI if needed
 string? posterUdi = null;

 // Only populate image if missing later, but we need UDI when creating a new content
 if (!existingIndex.ContainsKey(show.Id.ToString()))
 {
 posterUdi = await EnsurePosterAsync(show, ct);
 }

 var name = show.Name ?? $"Show {show.Id}";
 var summaryEn = show.Summary; // TVMaze provides HTML
 string? summaryDa = null;
 if (_opt.ImportCultures.Contains("da", StringComparer.OrdinalIgnoreCase))
 {
 summaryDa = await (_translator?.TranslateAsync(summaryEn, to: "da") ?? Task.FromResult(summaryEn));
 }

 // Invariant values
 var valuesInvariant = new List<object>
 {
 new { alias = "showId", value = show.Id.ToString() }
 };
 if (posterUdi != null)
 {
 valuesInvariant.Add(new { alias = "showImage", value = posterUdi });
 }
 // genres block list
 var genresValue = BuildGenresBlockList(show);
 if (genresValue is not null)
 {
 valuesInvariant.Add(new { alias = "showGenres", value = genresValue });
 }

 // Variant values per culture
 var variants = new List<object>();
 variants.Add(new
 {
 culture = "en-US",
 name,
 values = new List<object>
 {
 new { alias = "showSummary", value = (object?)summaryEn }
 }
 });
 if (_opt.ImportCultures.Contains("da", StringComparer.OrdinalIgnoreCase))
 {
 variants.Add(new
 {
 culture = "da",
 name,
 values = new List<object>
 {
 new { alias = "showSummary", value = (object?)summaryDa }
 }
 });
 }

 if (!existingIndex.TryGetValue(show.Id.ToString(), out var existing))
 {
 var payload = new
 {
 contentTypeAlias = "tVShow",
 parentKey = _opt.ShowsParentKey,
 values = valuesInvariant,
 variants = variants
 };
 var key = await _client.CreateContentAsync(payload, ct);
 if (_opt.PublishImmediately)
 {
 foreach (var c in _opt.ImportCultures)
 {
 await _client.PublishAsync(key, c, ct);
 }
 }
 return true;
 }
 else
 {
 var payload = new
 {
 values = valuesInvariant,
 variants = variants
 };
 await _client.UpdateContentAsync(existing.key, payload, ct);
 if (_opt.PublishImmediately)
 {
 foreach (var c in _opt.ImportCultures)
 {
 await _client.PublishAsync(existing.key, c, ct);
 }
 }
 return true;
 }
 }

 private async Task<string?> EnsurePosterAsync(TVMazeShow show, CancellationToken ct)
 {
 var url = show.Image?.Original ?? show.Image?.Medium;
 if (string.IsNullOrWhiteSpace(url)) return null;
 try
 {
 using var resp = await _http.GetAsync(url, ct);
 if (!resp.IsSuccessStatusCode) return null;
 var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
 var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
 await using var stream = await resp.Content.ReadAsStreamAsync(ct);
 return await _client.UploadMediaAsync(_opt.MediaFolderKey, fileName, contentType, stream, ct);
 }
 catch (HttpRequestException) { return null; }
 }

 private object? BuildGenresBlockList(TVMazeShow show)
 {
 if (_opt.GenreElementTypeKey is null) return null;
 var genres = show.Genres;
 if (genres is null || genres.Length ==0) return null;
 var items = new List<BlockListItem>();
 for (int i =0; i < genres.Length; i++)
 {
 var g = genres[i];
 var item = new BlockListItem
 {
 ContentTypeKey = _opt.GenreElementTypeKey.Value,
 Variants = new()
 {
 new BlockListVariant
 {
 Culture = "en-US",
 Name = g,
 Values = new()
 {
 new BlockListProperty{ Alias = "indexNumber", Value = (i+1).ToString() },
 new BlockListProperty{ Alias = "title", Value = g }
 }
 }
 }
 };
 items.Add(item);
 }
 return new
 {
 layout = new List<object>(),
 contentData = items
 };
 }
 }
}
