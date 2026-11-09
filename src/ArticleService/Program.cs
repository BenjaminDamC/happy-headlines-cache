using System.Text.Json;
using Npgsql;
using StackExchange.Redis;

// ArticleService — reads articles through the ArticleCache (Redis).
//
// The article DATABASE is scaled on the z-axis: the data is sharded by article id
// across two PostgreSQL shards, and this service asks the ArticleCoordinator which
// shard owns an article before reading it (never hard-coding the topology).
//
// The service ITSELF is scaled on the x-axis: several identical replicas run
// behind the article-lb (nginx) load balancer (see deploy/nginx.conf).
//
// The cache is filled OFFLINE (see the ArticleFill project), never on-demand.

var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "redis://localhost:6379";
var coordinatorUrl = Environment.GetEnvironmentVariable("COORDINATOR_URL") ?? "http://article-coordinator:4007";

// Cache hit-ratio counters (in-memory; reset on restart).
long cacheHits = 0;
long cacheMisses = 0;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:4001");
builder.Services.AddCors();
builder.Services.AddHttpClient("coordinator", c => c.BaseAddress = new Uri(coordinatorUrl));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

// Connect with retry (containers may start slowly).
var redis = await ConnectWithRetryAsync(redisUrl, "redis");
var coordinator = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("coordinator");

// z-axis: fetch the shard topology from the coordinator and open one data source
// per shard.
var shards = await GetShardsAsync(coordinator);
var shardSources = new Dictionary<string, NpgsqlDataSource>();
foreach (var shard in shards)
{
    shardSources[shard.Name] = NpgsqlDataSource.Create(shard.Connection);
    await WithRetryAsync(async () =>
    {
        await using var cmd = shardSources[shard.Name].CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync();
    }, $"postgres ({shard.Name})");
}

var db = redis.GetDatabase();
var server = redis.GetServer(redis.GetEndPoints().First());

app.MapGet("/health", () =>
    Results.Ok(new
    {
        status = "ok",
        service = "article-service",
        cache = redisUrl,
        shards = shards.Select(s => s.Name).ToArray(),
    }));

app.MapGet("/cache/size", () =>
{
    var keys = server.Keys(pattern: "article:*").ToArray();
    return Results.Ok(new { cachedArticles = keys.Length });
});

app.MapGet("/cache/hit-ratio", () =>
{
    var hits = Interlocked.Read(ref cacheHits);
    var misses = Interlocked.Read(ref cacheMisses);
    var total = hits + misses;
    var hitRatio = total == 0 ? 0.0 : (double)hits / total;
    return Results.Ok(new { service = "article-service", hits, misses, total, hitRatio });
});

app.MapGet("/articles", async () =>
{
    var cachedIds = new HashSet<string>(
        server.Keys(pattern: "article:*").Select(k => k.ToString().Split(':')[^1]));

    var articles = new List<ArticleListItem>();
    foreach (var (name, source) in shardSources)
    {
        await using var cmd = source.CreateCommand(
            "SELECT id, title, published_at FROM articles ORDER BY published_at DESC");
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(0);
            articles.Add(new ArticleListItem(
                id,
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                cachedIds.Contains(id.ToString())));
        }
    }

    return Results.Ok(new
    {
        articles = articles
            .OrderByDescending(a => a.PublishedAt)
            .Select(a => new { a.Id, a.Title, a.PublishedAt, a.Cached })
    });
});

app.MapGet("/articles/{id:int}", async (int id) =>
{
    var cached = await db.StringGetAsync($"article:{id}");
    if (!cached.IsNull)
    {
        Interlocked.Increment(ref cacheHits);
        var article = JsonSerializer.Deserialize<Article>(cached!, JsonDefaults.Web);
        return Results.Ok(new { source = "cache", article });
    }

    // Cache miss -> ask the coordinator which shard owns this article (z-axis).
    var route = JsonSerializer.Deserialize<RouteResult>(
        await coordinator.GetStringAsync($"/route/{id}"), JsonDefaults.Web)
        ?? throw new Exception("coordinator returned no route");

    var source = shardSources[route.Shard];
    await using var cmd = source.CreateCommand(
        "SELECT id, title, content, published_at FROM articles WHERE id = $1");
    cmd.Parameters.AddWithValue(id);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return Results.NotFound(new { error = "not found" });
    }

    Interlocked.Increment(ref cacheMisses);
    var dbArticle = new Article(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetFieldValue<DateTimeOffset>(3));
    return Results.Ok(new { source = "db", shard = route.Shard, article = dbArticle });
});

app.Run();

static async Task<List<Shard>> GetShardsAsync(HttpClient coordinator, int tries = 40)
{
    for (var i = 0; i < tries; i++)
    {
        try
        {
            var json = await coordinator.GetStringAsync("/shards");
            var result = JsonSerializer.Deserialize<ShardsResult>(json, JsonDefaults.Web);
            if (result?.Shards is { Count: > 0 })
            {
                Console.WriteLine($"[coordinator] connected, {result.Shards.Count} shard(s)");
                return result.Shards;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[coordinator] not ready: {ex.Message}");
        }
        await Task.Delay(1000);
    }
    throw new Exception("coordinator never connected");
}

static async Task<ConnectionMultiplexer> ConnectWithRetryAsync(string url, string label, int tries = 40)
{
    // StackExchange.Redis expects a bare "host:port", not a "redis://" URI.
    var endpoint = url.StartsWith("redis://") ? url["redis://".Length..]
        : url.StartsWith("rediss://") ? url["rediss://".Length..]
        : url;
    for (var i = 0; i < tries; i++)
    {
        try
        {
            var mux = await ConnectionMultiplexer.ConnectAsync(endpoint);
            Console.WriteLine($"{label} connected");
            return mux;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{label}] not ready: {ex.Message}");
            await Task.Delay(1000);
        }
    }
    throw new Exception($"{label} never connected");
}

static async Task WithRetryAsync(Func<Task> action, string label, int tries = 40)
{
    for (var i = 0; i < tries; i++)
    {
        try
        {
            await action();
            Console.WriteLine($"{label} connected");
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{label}] not ready: {ex.Message}");
            await Task.Delay(1000);
        }
    }
    throw new Exception($"{label} never connected");
}

internal record Article(int Id, string Title, string Content, DateTimeOffset PublishedAt);
internal record ArticleListItem(int Id, string Title, DateTimeOffset PublishedAt, bool Cached);
internal record Shard(string Name, string Connection);
internal record ShardsResult(string Strategy, List<Shard> Shards);
internal record RouteResult(int ArticleId, string Shard, string Connection);

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
