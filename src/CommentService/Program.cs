using System.Text.Json;
using Npgsql;
using StackExchange.Redis;

// CommentService — cache-miss with LRU eviction.
// Reads comments through the CommentCache (Redis). On a MISS it fetches from the
// database AND stores the result, tracking the most-recently-accessed articles in
// a sorted set. When more than 30 articles are cached, the least-recently-used one
// is evicted.

const int MaxCachedArticles = 30;
const string LruKey = "commentcache:lru";

var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "redis://localhost:6379";
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "Host=localhost;Port=5432;Database=comments;Username=hh;Password=hh";

// Cache hit-ratio counters (in-memory; reset on restart).
long cacheHits = 0;
long cacheMisses = 0;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:4002");
builder.Services.AddCors();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

var redis = await ConnectWithRetryAsync(redisUrl, "redis");
await using var dataSource = NpgsqlDataSource.Create(databaseUrl);
await WithRetryAsync(async () =>
{
    await using var cmd = dataSource.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync();
}, "postgres");

var db = redis.GetDatabase();

app.MapGet("/health", () =>
    Results.Ok(new { status = "ok", service = "comment-service", cache = redisUrl }));

app.MapGet("/cache/stats", async () =>
{
    var cachedArticles = await db.SortedSetLengthAsync(LruKey);
    return Results.Ok(new { cachedArticles, max = MaxCachedArticles });
});

app.MapGet("/cache/hit-ratio", () =>
{
    var hits = Interlocked.Read(ref cacheHits);
    var misses = Interlocked.Read(ref cacheMisses);
    var total = hits + misses;
    var hitRatio = total == 0 ? 0.0 : (double)hits / total;
    return Results.Ok(new { service = "comment-service", hits, misses, total, hitRatio });
});

app.MapGet("/articles/{articleId:int}/comments", async (int articleId) =>
{
    var cached = await db.StringGetAsync($"comments:{articleId}");
    if (!cached.IsNull)
    {
        Interlocked.Increment(ref cacheHits);
        // Touch → becomes most-recently-used in the LRU sorted set.
        await db.SortedSetAddAsync(LruKey, articleId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var comments = JsonSerializer.Deserialize<List<Comment>>(cached!, JsonDefaults.Web);
        return Results.Ok(new { source = "cache", comments });
    }

    Interlocked.Increment(ref cacheMisses);
    var dbComments = await GetCommentsFromDbAsync(dataSource, articleId);

    await db.StringSetAsync($"comments:{articleId}", JsonSerializer.Serialize(dbComments, JsonDefaults.Web));
    await db.SortedSetAddAsync(LruKey, articleId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    await EvictIfOverAsync(db);

    return Results.Ok(new { source = "db", comments = dbComments });
});

app.Run();

static async Task<List<Comment>> GetCommentsFromDbAsync(NpgsqlDataSource dataSource, int articleId)
{
    await using var cmd = dataSource.CreateCommand(
        "SELECT id, article_id, author, content, created_at FROM comments WHERE article_id = $1 ORDER BY created_at");
    cmd.Parameters.AddWithValue(articleId);
    await using var reader = await cmd.ExecuteReaderAsync();

    var comments = new List<Comment>();
    while (await reader.ReadAsync())
    {
        comments.Add(new Comment(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4)));
    }
    return comments;
}

static async Task EvictIfOverAsync(IDatabase db)
{
    var count = await db.SortedSetLengthAsync(LruKey);
    if (count > MaxCachedArticles)
    {
        var oldest = await db.SortedSetRangeByRankAsync(LruKey, 0, 0);
        if (oldest.Length > 0)
        {
            await db.KeyDeleteAsync($"comments:{oldest[0]}");
            await db.SortedSetRemoveAsync(LruKey, oldest[0]);
            Console.WriteLine($"[lru] evicted article {oldest[0]} ({count - 1}/{MaxCachedArticles} kept)");
        }
    }
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

internal record Comment(int Id, int ArticleId, string Author, string Content, DateTimeOffset CreatedAt);

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
