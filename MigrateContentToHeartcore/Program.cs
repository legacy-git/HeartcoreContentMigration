// See https://aka.ms/new-console-template for more information
using MigrateContentToHeartcore.DTOs;
using MigrateContentToHeartcore.Heartcore;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Formatting;
using System.Linq;

int page =0;

Uri ShowsAPI(int page) => new($"https://api.tvmaze.com/shows?page={page}");

HttpClient client = new();

ConcurrentDictionary<int, TVMazeShow> allShows = [];
Stopwatch totalTime = Stopwatch.StartNew();

while (true)
{
	var response = client.GetAsync(ShowsAPI(page++)).Result;
	var shows = response.Content.ReadAsAsync<TVMazeShow[]>(formatters: [new JsonMediaTypeFormatter()]).Result;
	try { response.EnsureSuccessStatusCode(); } catch { break; }
	if (shows.Length !=0)
	{
		foreach (var show in shows)
		{
			allShows.TryAdd(show.Id, show);
		}
	}
}
Console.WriteLine($"Total time to download:{totalTime.Elapsed}");
totalTime.Restart();

// Heartcore setup
var options = HeartcoreOptions.FromEnvironment();
if (string.IsNullOrWhiteSpace(options.ProjectAlias) || string.IsNullOrWhiteSpace(options.ApiKey) || options.ShowsParentKey == Guid.Empty)
{
	Console.WriteLine("Missing required Heartcore configuration in environment variables.");
	return;
}

var http = new HttpClient();
var heartcoreClient = new HeartcoreClient(http, options);
var translator = options.UseTranslation ? new Translator(new HttpClient(), options) : null;
var upsertService = new UpsertService(heartcoreClient, options, new HttpClient(), translator);

// Simple connectivity check to the content root/parent
try
{
	await heartcoreClient.ValidateParentAsync(options.ShowsParentKey, CancellationToken.None);
	Console.WriteLine("Heartcore connectivity OK: parent content is accessible.");
}
catch (Exception ex)
{
	Console.WriteLine($"Heartcore connectivity failed: {ex.Message}");
	return;
}

// Build existing index of shows by showId
var existingIndex = await heartcoreClient.GetChildrenIndexAsync(options.ShowsParentKey, CancellationToken.None);

if (options.DeliveryOnly)
{
	Console.WriteLine("Delivery-only mode enabled: skipping create/update/publish operations.");
	Console.WriteLine($"Found {existingIndex.Count} existing items under the parent (may be0). Exiting.");
	return;
}

var toProcess = allShows.Values.OrderBy(s => s.Id).Take(Math.Max(1, options.Take));
await Parallel.ForEachAsync(toProcess, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (show, ct) =>
{
	try
	{
		await upsertService.UpsertAsync(show, existingIndex, ct);
	}
	catch (Exception ex)
	{
		Console.WriteLine($"Failed processing show {show.Id} - {show.Name}: {ex.Message}");
	}
});

totalTime.Stop();
Console.WriteLine($"Total time to upload:{totalTime.Elapsed}");


