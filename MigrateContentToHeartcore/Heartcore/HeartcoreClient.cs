using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MigrateContentToHeartcore.Heartcore
{
 /// <summary>
 /// Thin wrapper around Umbraco Heartcore Management API endpoints used in the migration.
 /// Each method performs a single request; higher-level batching/parallelism lives outside.
 /// </summary>
 internal sealed class HeartcoreClient
 {
 private readonly HttpClient _http;
 private readonly HeartcoreOptions _options;
 private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
 {
 PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
 DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
 WriteIndented = false
 };

 public HeartcoreClient(HttpClient http, HeartcoreOptions options)
 {
 _http = http;
 _options = options;

 // Base configuration for all management API calls.
 _http.BaseAddress = new Uri("https://api.umbraco.io/");
 _http.DefaultRequestHeaders.Clear();
 _http.DefaultRequestHeaders.Add("Umb-Project-Alias", _options.ProjectAlias); // project scoping
 _http.DefaultRequestHeaders.Add("Api-Key", _options.ApiKey); // authentication
 _http.DefaultRequestHeaders.ConnectionClose = false; // encourage connection reuse for throughput
 _http.Timeout = TimeSpan.FromSeconds(300); // generous timeout for large multipart posts
 }

 /// <summary>
 /// Validates that the specified parent content node exists.
 /// </summary>
 public async Task ValidateParentAsync(Guid parentKey, CancellationToken ct)
 {
 using var resp = await _http.GetAsync($"content/{parentKey}", ct);
 await ThrowIfFailedAsync(resp, ct);
 }

 /// <summary>
 /// Builds an in-memory index of child content items under a parent keyed by the TVMaze showId.
 /// Minimizes duplicate creation attempts.
 /// </summary>
 public async Task<Dictionary<string, (Guid key, string name)>> GetChildrenIndexAsync(Guid parentKey, CancellationToken ct)
 {
 var result = new Dictionary<string, (Guid, string)>(StringComparer.OrdinalIgnoreCase);
 var url = $"content/{parentKey}/children?page=1&pageSize=500"; // large page size reduces round trips

 while (!string.IsNullOrEmpty(url))
 {
 using var resp = await _http.GetAsync(url, ct);
 if (resp.StatusCode == HttpStatusCode.NotFound) return result; // parent missing

 await ThrowIfFailedAsync(resp, ct);

 var json = await resp.Content.ReadAsStringAsync(ct);
 using var doc = JsonDocument.Parse(json);
 var root = doc.RootElement;

 if (root.TryGetProperty("_embedded", out var embedded) &&
 embedded.TryGetProperty("content", out var contentArray))
 {
 foreach (var item in contentArray.EnumerateArray())
 {
 if (item.TryGetProperty("_id", out var idEl) &&
 item.TryGetProperty("name", out var nameEl))
 {
 var key = idEl.GetGuid();
 string name = string.Empty;
 using (var e = nameEl.EnumerateObject())
 {
 if (e.MoveNext())
 {
 name = e.Current.Value.GetString() ?? string.Empty;
 }
 }

 if (item.TryGetProperty("showId", out var showIdProp) &&
 showIdProp.ValueKind == JsonValueKind.Object &&
 showIdProp.TryGetProperty("$invariant", out var invariantValue))
 {
 var showId = invariantValue.GetString();
 if (!string.IsNullOrEmpty(showId))
 {
 result[showId] = (key, name);
 }
 }
 }
 }
 }

 // Pagination: follow _links.next.href if present.
 url = null;
 if (root.TryGetProperty("_links", out var links) &&
 links.TryGetProperty("next", out var nextLink) &&
 nextLink.TryGetProperty("href", out var nextHref) &&
 nextHref.ValueKind == JsonValueKind.String)
 {
 url = nextHref.GetString();
 }
 }

 return result;
 }

 /// <summary>
 /// Create content without a file (image absent). Returns the guid key from the API response.
 /// </summary>
 public async Task<Guid> CreateContentAsync(object payload, CancellationToken ct)
 {
 var json = JsonSerializer.Serialize(payload, JsonOpts);
 using var resp = await _http.PostAsync("content", new StringContent(json, Encoding.UTF8, "application/json"), ct);
 await ThrowIfFailedAsync(resp, ct);

 var responseBody = await resp.Content.ReadAsStringAsync(ct);
 var result = JsonDocument.Parse(responseBody);
 var root = result.RootElement;

 if (root.TryGetProperty("_id", out var idProp))
 return idProp.GetGuid();
 if (root.TryGetProperty("id", out var idProp2))
 return idProp2.GetGuid();
 if (root.TryGetProperty("key", out var keyProp))
 return keyProp.GetGuid();

 throw new InvalidOperationException("Could not find content key in response");
 }

 /// <summary>
 /// Create content and upload its image in one multipart request to minimize round trips.
 /// </summary>
 public async Task<Guid> CreateContentWithFileAsync(
 object contentPayload,
 string filePropertyAlias,
 string filePropertyCulture,
 string fileName,
 string contentType,
 Stream fileStream,
 CancellationToken ct)
 {
 var boundary = "MultipartBoundary";
 using var form = new MultipartFormDataContent(boundary);

 var json = JsonSerializer.Serialize(contentPayload, JsonOpts);
 var jsonContent = new StringContent(json, Encoding.UTF8, "application/json");
 jsonContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
 {
 Name = "\"content\""
 };
 form.Add(jsonContent);

 var partName = $"{filePropertyAlias}.{filePropertyCulture}"; // e.g. showImage.$invariant
 var fileContent = new StreamContent(fileStream);
 fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
 fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
 {
 Name = $"\"{partName}\"",
 FileName = $"\"{fileName}\""
 };
 form.Add(fileContent);

 using var resp = await _http.PostAsync("content", form, ct);
 await ThrowIfFailedAsync(resp, ct);

 var body = await resp.Content.ReadAsStringAsync(ct);
 using var doc = JsonDocument.Parse(body);
 var root = doc.RootElement;
 if (root.TryGetProperty("_id", out var idProp)) return idProp.GetGuid();
 if (root.TryGetProperty("id", out var idProp2)) return idProp2.GetGuid();
 if (root.TryGetProperty("key", out var keyProp)) return keyProp.GetGuid();

 throw new InvalidOperationException("Could not find content key in response");
 }

 /// <summary>
 /// Publish a content item for a specific culture. Failures are ignored to keep migration progress resilient.
 /// </summary>
 public async Task PublishAsync(Guid key, string culture, CancellationToken ct)
 {
 var body = new { cultures = new[] { culture } };
 var json = JsonSerializer.Serialize(body, JsonOpts);
 using var resp = await _http.PutAsync($"content/{key}/publish", new StringContent(json, Encoding.UTF8, "application/json"), ct);
 // ignore non-success to avoid stopping migration
 }

 /// <summary>
 /// Delete content item (utility for cleanup / reruns).
 /// </summary>
 public async Task DeleteContentAsync(Guid key, CancellationToken ct)
 {
 try
 {
 using var resp = await _http.DeleteAsync($"content/{key}", ct);
 if (!resp.IsSuccessStatusCode)
 {
 var errorBody = await resp.Content.ReadAsStringAsync(ct);
 Console.WriteLine($" ?? Failed to delete content {key}: {resp.StatusCode}");
 }
 }
 catch (Exception ex)
 {
 Console.WriteLine($" ? Delete content exception: {ex.Message}");
 }
 }

 /// <summary>
 /// Uniform error handling to surface Umbraco-specific error details for easier debugging.
 /// </summary>
 private static async Task ThrowIfFailedAsync(HttpResponseMessage resp, CancellationToken ct)
 {
 if (resp.IsSuccessStatusCode)
 return;

 var body = await resp.Content.ReadAsStringAsync(ct);
 var req = resp.RequestMessage;

 string? nativeCode = null;
 string? nativeMessage = null;
 try
 {
 using var doc = JsonDocument.Parse(body);
 var root = doc.RootElement;
 if (root.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.Number)
 nativeCode = statusProp.GetInt32().ToString();
 if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.String)
 nativeMessage = errorProp.GetString();
 if (root.TryGetProperty("message", out var messageProp) && messageProp.ValueKind == JsonValueKind.String)
 nativeMessage = messageProp.GetString();
 if (root.TryGetProperty("detail", out var detailProp) && detailProp.ValueKind == JsonValueKind.String)
 nativeMessage = string.IsNullOrWhiteSpace(nativeMessage) ? detailProp.GetString() : $"{nativeMessage} | {detailProp.GetString()}";
 if (root.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
 {
 var title = titleProp.GetString();
 if (!string.IsNullOrWhiteSpace(title))
 nativeMessage = string.IsNullOrWhiteSpace(nativeMessage) ? title : $"{title}: {nativeMessage}";
 }
 }
 catch
 {
 // ignore JSON parse issues, include raw body
 }

 var msg = $"HTTP {(int)resp.StatusCode} {resp.StatusCode} when {req?.Method} {req?.RequestUri}.";
 if (!string.IsNullOrWhiteSpace(nativeCode) || !string.IsNullOrWhiteSpace(nativeMessage))
 msg += $" Umbraco error{(nativeCode is not null ? $" code {nativeCode}" : string.Empty)}{(nativeMessage is not null ? $", message: {nativeMessage}" : string.Empty)}.";
 msg += $" Body: {body}";

 throw new HttpRequestException(msg);
 }
 }
}
