using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;

// CommentService — cache-miss with LRU eviction.
// Reads comments through the CommentCache (Redis). On a MISS it fetches from the
// database AND stores the result, tracking the most-recently-accessed articles in
// a sorted set. When more than 30 articles are cached, the least-recently-used one
// is evicted.
//
// Fault isolation: new comments are moderated by the ProfanityService over HTTP,
// behind a hand-rolled circuit breaker. When the ProfanityService is down the
// breaker opens and comments are accepted unmoderated instead of failing.

const int MaxCachedArticles = 30;
const string LruKey = "commentcache:lru";

var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "redis://localhost:6379";
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "Host=localhost;Port=5432;Database=comments;Username=hh;Password=hh";
var profanityUrl = Environment.GetEnvironmentVariable("PROFANITY_URL") ?? "http://profanity-service:4006";
var otlpEndpoint = Environment.GetEnvironmentVariable("OTLP_ENDPOINT") ?? "http://jaeger:4318";

// Cache hit-ratio counters (in-memory; reset on restart).
long cacheHits = 0;
long cacheMisses = 0;
long requestCount = 0;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:4002");
builder.Services.AddCors();
builder.Services.AddHttpClient("profanity", c => c.BaseAddress = new Uri(profanityUrl));

// Monitoring — distributed tracing exported to Jaeger over OTLP/HTTP.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("comment-service"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o =>
        {
            o.Protocol = OtlpExportProtocol.HttpProtobuf;
            o.Endpoint = new Uri(otlpEndpoint);
        }));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

// Structured request log + counter, correlated to the active trace id.
app.Use(async (ctx, next) =>
{
    Interlocked.Increment(ref requestCount);
    var sw = Stopwatch.StartNew();
    await next();
    sw.Stop();
    var traceId = Activity.Current?.TraceId.ToString() ?? "n/a";
    Console.WriteLine(
        $"{{\"service\":\"comment-service\",\"traceId\":\"{traceId}\",\"method\":\"{ctx.Request.Method}\"," +
        $"\"path\":\"{ctx.Request.Path}\",\"status\":{ctx.Response.StatusCode},\"durationMs\":{sw.ElapsedMilliseconds}}}");
});

var redis = await ConnectWithRetryAsync(redisUrl, "redis");
await using var dataSource = NpgsqlDataSource.Create(databaseUrl);
await WithRetryAsync(async () =>
{
    await using var cmd = dataSource.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync();
}, "postgres");

var db = redis.GetDatabase();

// Circuit breaker guarding the ProfanityService call (fault isolation).
var breaker = new CircuitBreaker();

app.MapGet("/health", () =>
    Results.Ok(new { status = "ok", service = "comment-service", cache = redisUrl }));

app.MapGet("/circuit", () =>
    Results.Ok(new { service = "profanity-service", breaker = breaker.State }));

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

// Prometheus metrics (text exposition) — scraped by the Prometheus container.
app.MapGet("/metrics", () =>
{
    var hits = Interlocked.Read(ref cacheHits);
    var misses = Interlocked.Read(ref cacheMisses);
    var requests = Interlocked.Read(ref requestCount);
    var breakerState = breaker.State switch { "closed" => 0, "open" => 1, _ => 2 };
    return Results.Text(
        "# HELP happyheadlines_http_requests_total Total HTTP requests served.\n" +
        "# TYPE happyheadlines_http_requests_total counter\n" +
        $"happyheadlines_http_requests_total{{service=\"comment-service\"}} {requests}\n" +
        "# HELP happyheadlines_cache_hits_total Cache reads served from Redis.\n" +
        "# TYPE happyheadlines_cache_hits_total counter\n" +
        $"happyheadlines_cache_hits_total{{service=\"comment-service\"}} {hits}\n" +
        "# HELP happyheadlines_cache_misses_total Cache reads that fell through to the database.\n" +
        "# TYPE happyheadlines_cache_misses_total counter\n" +
        $"happyheadlines_cache_misses_total{{service=\"comment-service\"}} {misses}\n" +
        "# HELP happyheadlines_circuit_breaker_state 0=closed, 1=open, 2=half-open.\n" +
        "# TYPE happyheadlines_circuit_breaker_state gauge\n" +
        $"happyheadlines_circuit_breaker_state{{service=\"comment-service\"}} {breakerState}\n",
        "text/plain; version=0.0.4");
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

app.MapPost("/articles/{articleId:int}/comments", async (int articleId, CommentRequest req, IHttpClientFactory httpFactory) =>
{
    if (string.IsNullOrWhiteSpace(req.Author) || string.IsNullOrWhiteSpace(req.Content))
        return Results.BadRequest(new { error = "author and content are required" });

    // Moderate the comment through the ProfanityService, behind the circuit
    // breaker. If the breaker is open (ProfanityService down) we degrade
    // gracefully: accept the comment but leave it unmoderated, so an outage in
    // that swimlane does not block commenting.
    var moderated = true;

    if (breaker.IsOpen)
    {
        moderated = false;
        Console.WriteLine("[circuit] OPEN — skipping profanity check, accepting comment unmoderated");
    }
    else
    {
        try
        {
            var client = httpFactory.CreateClient("profanity");
            var response = await client.GetAsync($"/check?text={Uri.EscapeDataString(req.Content)}");
            response.EnsureSuccessStatusCode();
            var check = JsonSerializer.Deserialize<ProfanityResult>(
                await response.Content.ReadAsStringAsync(), JsonDefaults.Web);

            if (check?.Flagged == true)
                return Results.BadRequest(new { error = "comment rejected: contains profanity", matches = check.Matches });

            breaker.RecordSuccess();
        }
        catch (Exception ex)
        {
            breaker.RecordFailure();
            moderated = false;
            Console.WriteLine($"[circuit] profanity check failed: {ex.Message} (state={breaker.State})");
        }
    }

    // Insert the comment, then invalidate its cache entry so the next read refills.
    await using var cmd = dataSource.CreateCommand(
        "INSERT INTO comments (article_id, author, content) VALUES ($1, $2, $3) RETURNING id, created_at");
    cmd.Parameters.AddWithValue(articleId);
    cmd.Parameters.AddWithValue(req.Author.Trim());
    cmd.Parameters.AddWithValue(req.Content.Trim());
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    var id = reader.GetInt32(0);
    var createdAt = reader.GetFieldValue<DateTimeOffset>(1);

    await db.KeyDeleteAsync($"comments:{articleId}");
    await db.SortedSetRemoveAsync(LruKey, articleId);

    return Results.Ok(new
    {
        id,
        articleId,
        author = req.Author.Trim(),
        content = req.Content.Trim(),
        moderated,
        createdAt,
    });
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
internal record CommentRequest(string Author, string Content);
internal record ProfanityResult(bool Flagged, string[] Matches);

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
