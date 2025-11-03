using System.Net.Http.Json;
using System.Text.Json;

namespace MigrateContentToHeartcore.Heartcore
{
 internal sealed class Translator
 {
 private readonly HttpClient _http;
 private readonly HeartcoreOptions _opt;

 public Translator(HttpClient http, HeartcoreOptions opt)
 {
 _http = http; _opt = opt;
 if (_opt.UseTranslation)
 {
 _http.BaseAddress = new Uri(_opt.TranslatorEndpoint);
 _http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _opt.TranslatorKey!);
 if (!string.IsNullOrWhiteSpace(_opt.TranslatorRegion))
 _http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Region", _opt.TranslatorRegion!);
 }
 }

 public async Task<string?> TranslateAsync(string? text, string to, string from = "en")
 {
 if (!_opt.UseTranslation || string.IsNullOrWhiteSpace(text)) return text;
 var route = $"/translate?api-version=3.0&from={from}&to={to}";
 var body = new[] { new { Text = text } };
 using var resp = await _http.PostAsJsonAsync(route, body);
 if (!resp.IsSuccessStatusCode) return text;
 var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
 try
 {
 var translated = json[0].GetProperty("translations")[0].GetProperty("text").GetString();
 return translated ?? text;
 }
 catch { return text; }
 }
 }
}
