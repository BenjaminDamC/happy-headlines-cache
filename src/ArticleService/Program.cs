using System.Text.Json;
using Npgsql;
using StackExchange.Redis;

// ArticleService — reads articles through the ArticleCache (Redis).
// The cache is filled OFFLINE (see the ArticleFill project), never on-demand: this
// service only reads Redis and falls back to the database on a miss (e.g. an
// article outside the 14-day window).

var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "redis://localhost:6379";
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "Host=localhost;Port=5432;Database=articles;Username=hh;Password=hh";

// Cache hit-ratio counters (in-memory; reset on restart).
long cacheHits = 0;
long cacheMisses = 0;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:4001");
builder.Services.AddCors();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

// Connect with retry (containers may start slowly).
var redis = await ConnectWithRetryAsync(redisUrl, "redis");
await using var dataSource = NpgsqlDataSource.Create(databaseUrl);
await WithRetryAsync(async () =>
{
    await using var cmd = dataSource.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync();
}, "postgres");

var db = redis.GetDatabase();
var server = redis.GetServer(redis.GetEndPoints().First());

app.MapGet("/health", () =>
    Results.Ok(new { status = "ok", service = "article-service", cache = redisUrl }));

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
    await using var cmd = dataSource.CreateCommand(
        "SELECT id, title, published_at FROM articles ORDER BY published_at DESC");
    await using var reader = await cmd.ExecuteReaderAsync();

    var cachedIds = new HashSet<string>(
        server.Keys(pattern: "article:*").Select(k => k.ToString().Split(':')[^1]));

    var articles = new List<object>();
    while (await reader.ReadAsync())
    {
        var id = reader.GetInt32(0);
        articles.Add(new
        {
            id,
            title = reader.GetString(1),
            publishedAt = reader.GetFieldValue<DateTimeOffset>(2),
            cached = cachedIds.Contains(id.ToString()),
        });
    }
    return Results.Ok(new { articles });
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

    await using var cmd = dataSource.CreateCommand(
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
    return Results.Ok(new { source = "db", article = dbArticle });
});

app.Run();

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

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
