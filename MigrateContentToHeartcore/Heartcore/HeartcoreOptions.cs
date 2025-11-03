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
 public Guid? GenreElementTypeKey { get; init; }
 public string Culture { get; init; } = "en-US";
 public string[] ImportCultures { get; init; } = new[] { "en-US" };
 public bool PublishImmediately { get; init; }
 public int MaxDegreeOfParallelism { get; init; } =8;
 public int Take { get; init; } =1; // default: test with1
 public bool DeliveryOnly { get; init; } = false; // default: allow writes unless explicitly set to true

 // Azure Translator
 public string? TranslatorKey { get; init; }
 public string? TranslatorRegion { get; init; }
 public string TranslatorEndpoint { get; init; } = "https://api.cognitive.microsofttranslator.com";
 public bool UseTranslation => !string.IsNullOrWhiteSpace(TranslatorKey);

 public static HeartcoreOptions FromEnvironment()
 {
 var projectAlias = Environment.GetEnvironmentVariable("HEARTCORE_PROJECT_ALIAS") ?? string.Empty;
 var apiKey = Environment.GetEnvironmentVariable("HEARTCORE_MANAGEMENT_API_KEY") ?? string.Empty;
 var parentKey = Environment.GetEnvironmentVariable("HEARTCORE_SHOWS_PARENT_KEY");
 var mediaFolderKey = Environment.GetEnvironmentVariable("HEARTCORE_MEDIA_FOLDER_KEY");
 var genreKey = Environment.GetEnvironmentVariable("HEARTCORE_GENRE_ELEMENT_KEY");
 var culture = Environment.GetEnvironmentVariable("HEARTCORE_CULTURE") ?? "en-US";
 var cultures = Environment.GetEnvironmentVariable("HEARTCORE_IMPORT_CULTURES");
 var publish = (Environment.GetEnvironmentVariable("HEARTCORE_PUBLISH_IMMEDIATELY") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);
 var degreeStr = Environment.GetEnvironmentVariable("HEARTCORE_MAX_DEGREE");
 var takeStr = Environment.GetEnvironmentVariable("HEARTCORE_TAKE");
 var deliveryOnlyEnv = Environment.GetEnvironmentVariable("HEARTCORE_DELIVERY_ONLY");
 int degree =8;
 if (!string.IsNullOrWhiteSpace(degreeStr) && int.TryParse(degreeStr, out var parsed)) degree = parsed;
 int take =1;
 if (!string.IsNullOrWhiteSpace(takeStr) && int.TryParse(takeStr, out var parsedTake)) take = parsedTake;

 // DeliveryOnly: default false; honor explicit true/false values
 bool deliveryOnly = false;
 if (!string.IsNullOrWhiteSpace(deliveryOnlyEnv))
 {
 if (bool.TryParse(deliveryOnlyEnv, out var parsedBool))
 {
 deliveryOnly = parsedBool;
 }
 else if (string.Equals(deliveryOnlyEnv, "1") || deliveryOnlyEnv.Equals("yes", StringComparison.OrdinalIgnoreCase))
 {
 deliveryOnly = true;
 }
 else if (string.Equals(deliveryOnlyEnv, "0") || deliveryOnlyEnv.Equals("no", StringComparison.OrdinalIgnoreCase))
 {
 deliveryOnly = false;
 }
 }

 var translatorKey = Environment.GetEnvironmentVariable("AZURE_TRANSLATOR_KEY");
 var translatorRegion = Environment.GetEnvironmentVariable("AZURE_TRANSLATOR_REGION");
 var translatorEndpoint = Environment.GetEnvironmentVariable("AZURE_TRANSLATOR_ENDPOINT") ?? "https://api.cognitive.microsofttranslator.com";

 return new HeartcoreOptions
 {
 ProjectAlias = projectAlias,
 ApiKey = apiKey,
 ShowsParentKey = string.IsNullOrWhiteSpace(parentKey) ? Guid.Empty : Guid.Parse(parentKey),
 MediaFolderKey = string.IsNullOrWhiteSpace(mediaFolderKey) ? Guid.Empty : Guid.Parse(mediaFolderKey),
 GenreElementTypeKey = string.IsNullOrWhiteSpace(genreKey) ? null : Guid.Parse(genreKey),
 Culture = culture,
 ImportCultures = !string.IsNullOrWhiteSpace(cultures) ? cultures.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : new[] { culture },
 PublishImmediately = publish,
 MaxDegreeOfParallelism = degree,
 Take = take,
 TranslatorKey = translatorKey,
 TranslatorRegion = translatorRegion,
 TranslatorEndpoint = translatorEndpoint,
 DeliveryOnly = deliveryOnly
 };
 }
 }
}
