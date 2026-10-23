using System.Text.Json;
using Npgsql;
using StackExchange.Redis;

// ArticleCache OFFLINE fill — the background process that periodically copies the
// latest 14 days of articles from the database into Redis. This is the key
// difference from the CommentCache: the cache is pre-populated here, so the
// service never writes to it (a read of a non-cached article falls through to DB).

var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "redis://localhost:6379";
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "Host=localhost;Port=5432;Database=articles;Username=hh;Password=hh";
var intervalMs = int.Parse(Environment.GetEnvironmentVariable("FILL_INTERVAL_MS") ?? "30000");

var redis = await ConnectWithRetryAsync(redisUrl, "redis");
await using var dataSource = NpgsqlDataSource.Create(databaseUrl);
await WithRetryAsync(async () =>
{
    await using var cmd = dataSource.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync();
}, "postgres");

var db = redis.GetDatabase();
await FillAsync(db, dataSource);

using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
while (await timer.WaitForNextTickAsync())
{
    await FillAsync(db, dataSource);
}

static async Task<int> FillAsync(IDatabase db, NpgsqlDataSource dataSource)
{
    await using var cmd = dataSource.CreateCommand(
        "SELECT id, title, content, published_at FROM articles " +
        "WHERE published_at >= NOW() - interval '14 days' " +
        "ORDER BY published_at DESC");
    await using var reader = await cmd.ExecuteReaderAsync();

    var articles = new List<Article>();
    while (await reader.ReadAsync())
    {
        articles.Add(new Article(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3)));
    }

    var json = JsonDefaults.Web;
    foreach (var a in articles)
    {
        await db.StringSetAsync($"article:{a.Id}", JsonSerializer.Serialize(a, json));
    }

    Console.WriteLine($"[fill] cached {articles.Count} articles from the last 14 days");
    return articles.Count;
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

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
