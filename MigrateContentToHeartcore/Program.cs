// See https://aka.ms/new-console-template for more information
using MigrateContentToHeartcore.DTOs;
using MigrateContentToHeartcore.Heartcore;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

Console.WriteLine("🚀 Starting TVMaze to Heartcore Migration\n");

// Remove HTTP connection limits for maximum parallelism
ServicePointManager.DefaultConnectionLimit = 1000;
ServicePointManager.Expect100Continue = false;
ServicePointManager.UseNagleAlgorithm = false;

var downloadTimer = Stopwatch.StartNew();
int page = 0;
Uri ShowsAPI(int p) => new($"https://api.tvmaze.com/shows?page={p}");

// Optimized HTTP client for TVMaze (HTTP/2 + decompression)
var tvMazeHttp = new HttpClient(new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
    MaxConnectionsPerServer = 256,
    EnableMultipleHttp2Connections = true
})
{
    DefaultRequestVersion = HttpVersion.Version20,
    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
};

var allShows = new List<TVMazeShow>(90000);
var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// Download all shows from TVMaze (streaming)
while (true)
{
    using var response = await tvMazeHttp.GetAsync(ShowsAPI(page++), HttpCompletionOption.ResponseHeadersRead);
    if (!response.IsSuccessStatusCode) break;

    await using var stream = await response.Content.ReadAsStreamAsync();
    var shows = await JsonSerializer.DeserializeAsync<TVMazeShow[]>(stream, jsonOpts);
    if (shows is null || shows.Length == 0) break;

    allShows.AddRange(shows);

    if (page % 10 == 0) Console.WriteLine($"Downloaded {allShows.Count} shows...");
}

downloadTimer.Stop();
Console.WriteLine($"✅ Downloaded {allShows.Count} shows in {downloadTimer.Elapsed:mm\\:ss}\n");

// Heartcore setup
var options = HeartcoreOptions.FromEnvironment();
if (string.IsNullOrWhiteSpace(options.ProjectAlias) || string.IsNullOrWhiteSpace(options.ApiKey) || options.ShowsParentKey == Guid.Empty)
{
    Console.WriteLine("❌ Missing required Heartcore configuration.");
    return;
}

// Optimized HTTP client for Heartcore (HTTP/2 + pooling)
var managementHttp = new HttpClient(new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
    MaxConnectionsPerServer = 256,
    EnableMultipleHttp2Connections = true
})
{
    DefaultRequestVersion = HttpVersion.Version20,
    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
    Timeout = TimeSpan.FromMinutes(5)
};
var heartcoreClient = new HeartcoreClient(managementHttp, options);

// Separate HTTP client for images (avoid head-of-line blocking content calls)
var mediaDownloadHttp = new HttpClient(new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
    MaxConnectionsPerServer = 256
})
{
    DefaultRequestVersion = HttpVersion.Version20,
    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
    Timeout = TimeSpan.FromMinutes(5)
};
var upsertService = new UpsertService(heartcoreClient, options, mediaDownloadHttp);

// Connectivity check
try
{
    await heartcoreClient.ValidateParentAsync(options.ShowsParentKey, CancellationToken.None);
    Console.WriteLine("✅ Heartcore connectivity OK\n");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Heartcore connectivity failed: {ex.Message}");
    return;
}

// Get existing shows
Console.WriteLine("📊 Checking for existing shows...");
var baseIndex = await heartcoreClient.GetChildrenIndexAsync(options.ShowsParentKey, CancellationToken.None);
var existingIndex = new ConcurrentDictionary<string, (Guid key, string name)>(baseIndex, StringComparer.OrdinalIgnoreCase);
Console.WriteLine($"Found {existingIndex.Count} existing shows\n");

// Lightweight rate limiter (token bucket) honoring HEARTCORE_RPS
var rps = Math.Max(1, options.RequestsPerSecond);
var bucket = rps; // tokens available
var lastRefill = Stopwatch.StartNew();
var bucketLock = new object();

void ConsumeToken()
{
    while (true)
    {
        lock (bucketLock)
        {
            var elapsed = lastRefill.Elapsed;
            if (elapsed >= TimeSpan.FromSeconds(1))
            {
                bucket = rps; // refill each second
                lastRefill.Restart();
            }
            if (bucket > 0)
            {
                bucket--;
                return;
            }
        }
        Thread.Sleep(5);
    }
}

// Process shows
var toProcess = options.Take > 0
    ? allShows.OrderBy(s => s.Id).Take(options.Take)
    : allShows.OrderBy(s => s.Id);

Console.WriteLine($"🔄 Processing {(options.Take > 0 ? options.Take : allShows.Count)} shows with {options.MaxDegreeOfParallelism} parallel tasks...\n");

var createdShowKeys = new ConcurrentBag<Guid>();
var uploadTimer = Stopwatch.StartNew();
var processed = 0;
var skipped = 0;

await Parallel.ForEachAsync(toProcess, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (show, ct) =>
{
    try
    {
        // Acquire rate-limit token before making management API requests
        ConsumeToken();
        var key = await upsertService.UpsertAndGetKeyAsync(show, existingIndex, ct);
        if (key.HasValue)
        {
            createdShowKeys.Add(key.Value);
            existingIndex.TryAdd(show.Id.ToString(), (key.Value, show.Name ?? $"Show {show.Id}"));
            Interlocked.Increment(ref processed);
        }
        else
        {
            Interlocked.Increment(ref skipped);
        }

        var total = processed + skipped;
        if (total % 500 == 0)
        {
            Console.WriteLine($"Progress: {total} shows processed ({processed} created, {skipped} skipped)");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed: {show.Id} - {show.Name}: {ex.Message}");
    }
});

uploadTimer.Stop();
Console.WriteLine($"\n✅ Upload complete: {processed} created, {skipped} skipped in {uploadTimer.Elapsed:mm\\:ss}");

// Batch publish
if (options.PublishImmediately && createdShowKeys.Count > 0)
{
    Console.WriteLine($"\n📢 Publishing {createdShowKeys.Count} shows...");
    var publishTimer = Stopwatch.StartNew();

    await Parallel.ForEachAsync(createdShowKeys, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (key, ct) =>
    {
        foreach (var culture in options.ImportCultures)
        {
            ConsumeToken();
            await heartcoreClient.PublishAsync(key, culture, ct);
        }
    });

    publishTimer.Stop();
    Console.WriteLine($"✅ Published {createdShowKeys.Count} shows in {publishTimer.Elapsed:mm\\:ss}");
}

var totalTime = downloadTimer.Elapsed + uploadTimer.Elapsed + (options.PublishImmediately ? TimeSpan.Zero : TimeSpan.Zero);
Console.WriteLine($"\n🎉 Total migration time: {totalTime:hh\\:mm\\:ss}");
Console.WriteLine($"📊 Stats: {allShows.Count} total shows, {processed} migrated, {skipped} skipped");



