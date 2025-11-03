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

            // Management API (Heartcore) base. We intentionally avoid the legacy "/umbraco/management/api/v1" path.
            // For Delivery (read-only) the base would be https://cdn.umbraco.io/, which we are NOT using here.
            _http.BaseAddress = new Uri("https://api.umbraco.io/");
            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("Umb-Project-Alias", _options.ProjectAlias);
            _http.DefaultRequestHeaders.Add("Api-Key", _options.ApiKey);
            _http.Timeout = TimeSpan.FromSeconds(100);
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

            // Prefer the Management API route shape: /content/{parentKey}/children
            var primaryUrl = $"content/{parentKey}/children?skip=0&take=200"; // route shape B (preferred)
            // Fallback to the alternative route shape if the server supports it
            var fallbackUrl = $"content/children?parentKey={parentKey}&skip=0&take=200"; // route shape A (alternative)

            var url = primaryUrl;
            var triedFallback = false;

            while (true)
            {
                using var resp = await _http.GetAsync(url, ct);

                if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.BadRequest)
                {
                    // Some deployments return400 when hitting an unsupported route shape (e.g., interpreting "children" as an id)
                    if (!triedFallback && url == primaryUrl)
                    {
                        triedFallback = true;
                        url = fallbackUrl;
                        continue;
                    }

                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        // No children
                        return result;
                    }
                }

                await ThrowIfFailedAsync(resp, ct);

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var key = item.GetProperty("key").GetGuid();
                        var name = item.GetProperty("name").GetString() ?? string.Empty;
                        if (item.TryGetProperty("properties", out var props))
                        {
                            foreach (var prop in props.EnumerateArray())
                            {
                                var alias = prop.GetProperty("alias").GetString();
                                if (string.Equals(alias, "showId", StringComparison.OrdinalIgnoreCase))
                                {
                                    var value = prop.GetProperty("value").GetString();
                                    if (!string.IsNullOrEmpty(value))
                                    {
                                        result[value] = (key, name);
                                    }
                                }
                            }
                        }
                    }
                }

                // Pagination
                if (root.TryGetProperty("pagination", out var pag) &&
                    pag.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String)
                {
                    var nextUrl = next.GetString();
                    if (string.IsNullOrEmpty(nextUrl)) break;
                    url = nextUrl!; // could be absolute or relative
                }
                else
                {
                    break;
                }
            }

            return result;
        }

        // Media upload via Management API base
        public async Task<string?> UploadMediaAsync(Guid parentFolderKey, string fileName, string contentType, Stream content, CancellationToken ct)
        {
            using var form = new MultipartFormDataContent();
            var streamContent = new StreamContent(content);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(streamContent, name: "file", fileName: fileName);
            form.Add(new StringContent(parentFolderKey.ToString()), name: "parentKey");

            using var resp = await _http.PostAsync("media/upload", form, ct);
            await ThrowIfFailedAsync(resp, ct);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (json.TryGetProperty("udi", out var udiEl) && udiEl.ValueKind == JsonValueKind.String)
                return udiEl.GetString();
            if (json.TryGetProperty("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
            {
                var key = keyEl.GetString();
                return key is null ? null : $"umb://media/{key}";
            }
            return null;
        }

        // Create content using Management API /content
        public async Task<Guid> CreateContentAsync(object payload, CancellationToken ct)
        {
            using var resp = await _http.PostAsync("content", new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"), ct);
            await ThrowIfFailedAsync(resp, ct);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return json.GetProperty("key").GetGuid();
        }

        // Update content using Management API /content/{key}
        public async Task UpdateContentAsync(Guid key, object payload, CancellationToken ct)
        {
            using var resp = await _http.PutAsync($"content/{key}", new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"), ct);
            await ThrowIfFailedAsync(resp, ct);
        }

        // Publish using Management API /content/{key}/publish
        public async Task PublishAsync(Guid key, string culture, CancellationToken ct)
        {
            var body = new { cultures = new[] { culture } };
            using var resp = await _http.PostAsync($"content/{key}/publish", new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"), ct);
            await ThrowIfFailedAsync(resp, ct);
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
