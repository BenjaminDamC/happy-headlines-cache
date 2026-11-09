using System.Text.Json;
using Npgsql;
using StackExchange.Redis;

// ArticleCache OFFLINE fill — the background process that periodically copies the
// latest 14 days of articles from the database into Redis. This is the key
// difference from the CommentCache: the cache is pre-populated here, so the
// service never writes to it (a read of a non-cached article falls through to DB).
//
// Since the article database is sharded on the z-axis, the fill reads EVERY shard
// and writes each article into the single (global) Redis cache under its globally
// unique id.

var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "redis://localhost:6379";
var intervalMs = int.Parse(Environment.GetEnvironmentVariable("FILL_INTERVAL_MS") ?? "30000");

var shardNames = (Environment.GetEnvironmentVariable("SHARD_NAMES") ?? "article-db-1,article-db-2")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var connTemplate = Environment.GetEnvironmentVariable("SHARD_CONN_TEMPLATE")
    ?? "Host={0};Port=5432;Database=articles;Username=hh;Password=hh";

var redis = await ConnectWithRetryAsync(redisUrl, "redis");
var dataSources = new List<NpgsqlDataSource>();
foreach (var name in shardNames)
{
    var source = NpgsqlDataSource.Create(string.Format(connTemplate, name));
    dataSources.Add(source);
    await WithRetryAsync(async () =>
    {
        await using var cmd = source.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync();
    }, $"postgres ({name})");
}

var db = redis.GetDatabase();
await FillAsync(db, dataSources);

using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
while (await timer.WaitForNextTickAsync())
{
    await FillAsync(db, dataSources);
}

static async Task<int> FillAsync(IDatabase db, List<NpgsqlDataSource> dataSources)
{
    var json = JsonDefaults.Web;
    var written = 0;

    foreach (var dataSource in dataSources)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT id, title, content, published_at FROM articles " +
            "WHERE published_at >= NOW() - interval '14 days' " +
            "ORDER BY published_at DESC");
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var article = new Article(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3));
            await db.StringSetAsync($"article:{article.Id}", JsonSerializer.Serialize(article, json));
            written++;
        }
    }

    Console.WriteLine($"[fill] cached {written} articles from the last 14 days across {dataSources.Count} shard(s)");
    return written;
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
