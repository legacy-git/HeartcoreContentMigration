using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MigrateContentToHeartcore.Heartcore
{
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

            _http.BaseAddress = new Uri("https://api.umbraco.io/");
            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("Umb-Project-Alias", _options.ProjectAlias);
            _http.DefaultRequestHeaders.Add("Api-Key", _options.ApiKey);
            _http.DefaultRequestHeaders.ConnectionClose = false; // Keep connections alive
            _http.Timeout = TimeSpan.FromSeconds(300); // Increase timeout for bulk operations
        }

        public async Task ValidateParentAsync(Guid parentKey, CancellationToken ct)
        {
            // Validate against the Management API using /content/{id}
            using var resp = await _http.GetAsync($"content/{parentKey}", ct);
            await ThrowIfFailedAsync(resp, ct);
        }

        // Build an index of existing children keyed by showId using Management API /content children endpoints
        public async Task<Dictionary<string, (Guid key, string name)>> GetChildrenIndexAsync(Guid parentKey, CancellationToken ct)
        {
            var result = new Dictionary<string, (Guid, string)>(StringComparer.OrdinalIgnoreCase);
            var url = $"content/{parentKey}/children?page=1&pageSize=500"; // increased page size

            while (!string.IsNullOrEmpty(url))
            {
                using var resp = await _http.GetAsync(url, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) return result;
                
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

                // Check for pagination
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

        // Media upload via Management API - create image media item with file
        public async Task<string?> UploadMediaAsync(Guid parentFolderKey, string fileName, string contentType, Stream content, CancellationToken ct)
        {
            try
            {
                var mediaName = Path.GetFileNameWithoutExtension(fileName);
                
                // Create multipart form with the exact format from documentation
                var boundary = "MultipartBoundary";
                using var form = new MultipartFormDataContent(boundary);
                
                // Add content JSON part - media metadata with file reference
                var mediaJson = JsonSerializer.Serialize(new
                {
                    mediaTypeAlias = "Image",
                    parentId = parentFolderKey.ToString(),
                    name = mediaName,
                    umbracoFile = new Dictionary<string, string> { ["$invariant"] = fileName }
                }, JsonOpts);
                
                var jsonContent = new StringContent(mediaJson, Encoding.UTF8, "application/json");
                jsonContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
                {
                    Name = "\"content\""
                };
                form.Add(jsonContent);
                
                // Add file part with name format: propertyAlias.culture
                var fileContent = new StreamContent(content);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
                {
                    Name = "\"umbracoFile.$invariant\"",
                    FileName = $"\"{fileName}\""
                };
                form.Add(fileContent);
                
                var response = await _http.PostAsync("media", form, ct);
                if (!response.IsSuccessStatusCode) return null;
                
                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                
                return root.TryGetProperty("_id", out var idEl)
                    ? $"umb://media/{idEl.GetString()}"
                    : null;
            }
            catch
            {
                return null;
            }
        }

        // Create content using Management API /content
        public async Task<Guid> CreateContentAsync(object payload, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            using var resp = await _http.PostAsync("content", new StringContent(json, Encoding.UTF8, "application/json"), ct);
            await ThrowIfFailedAsync(resp, ct);
            
            var responseBody = await resp.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;
            
            // Heartcore returns _id, not key
            if (root.TryGetProperty("_id", out var idProp))
                return idProp.GetGuid();
            if (root.TryGetProperty("id", out var idProp2))
                return idProp2.GetGuid();
            if (root.TryGetProperty("key", out var keyProp))
                return keyProp.GetGuid();
                
            throw new InvalidOperationException($"Could not find content key in response");
        }

        // New: Create content with file in single multipart request
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

            // JSON content part
            var json = JsonSerializer.Serialize(contentPayload, JsonOpts);
            var jsonContent = new StringContent(json, Encoding.UTF8, "application/json");
            jsonContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "\"content\""
            };
            form.Add(jsonContent);

            // File part: propertyAlias.culture
            var partName = $"{filePropertyAlias}.{filePropertyCulture}";
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

        // Update content using Management API /content/{key}
        public async Task UpdateContentAsync(Guid key, object payload, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            using var resp = await _http.PutAsync($"content/{key}", new StringContent(json, Encoding.UTF8, "application/json"), ct);
            await ThrowIfFailedAsync(resp, ct);
        }

        // Publish using Management API /content/{key}/publish
        public async Task PublishAsync(Guid key, string culture, CancellationToken ct)
        {
            var body = new { cultures = new[] { culture } };
            var json = JsonSerializer.Serialize(body, JsonOpts);
            using var resp = await _http.PutAsync($"content/{key}/publish", new StringContent(json, Encoding.UTF8, "application/json"), ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Silently fail - don't stop the migration
            }
        }

        // Delete content item
        public async Task DeleteContentAsync(Guid key, CancellationToken ct)
        {
            try
            {
                using var resp = await _http.DeleteAsync($"content/{key}", ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var errorBody = await resp.Content.ReadAsStringAsync(ct);
                    Console.WriteLine($"   ?? Failed to delete content {key}: {resp.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ? Delete content exception: {ex.Message}");
            }
        }

        // Delete media item
        public async Task DeleteMediaAsync(Guid key, CancellationToken ct)
        {
            try
            {
                using var resp = await _http.DeleteAsync($"media/{key}", ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var errorBody = await resp.Content.ReadAsStringAsync(ct);
                    Console.WriteLine($"   ?? Failed to delete media {key}: {resp.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ? Delete media exception: {ex.Message}");
            }
        }

        // Get all media items in a folder
        public async Task<List<Guid>> GetMediaInFolderAsync(Guid folderKey, CancellationToken ct)
        {
            var result = new List<Guid>();
            try
            {
                var url = $"media/{folderKey}/children?page=1&pageSize=500";
                
                while (!string.IsNullOrEmpty(url))
                {
                    using var resp = await _http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode) break;
                    
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    
                    if (root.TryGetProperty("_embedded", out var embedded) && 
                        embedded.TryGetProperty("media", out var mediaArray))
                    {
                        foreach (var item in mediaArray.EnumerateArray())
                        {
                            if (item.TryGetProperty("_id", out var idEl))
                            {
                                result.Add(idEl.GetGuid());
                            }
                        }
                    }
                    
                    // Check for next page
                    url = null;
                    if (root.TryGetProperty("_links", out var links) && 
                        links.TryGetProperty("next", out var nextLink) && 
                        nextLink.TryGetProperty("href", out var nextHref))
                    {
                        url = nextHref.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ? Failed to get media in folder: {ex.Message}");
            }
            
            return result;
        }

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
