using System.Diagnostics;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ArticleCoordinator — the z-axis "coordination component".
//
// Scaling the ArticleService database on the z-axis (sharding by key) means the
// article data is split across several database shards, and SOMETHING has to know
// which shard a given article lives on. This service owns that knowledge: it
// routes an article id to its shard, so the ArticleService never hard-codes the
// topology. Add a shard and only this service needs to change.

var shardNames = (Environment.GetEnvironmentVariable("SHARD_NAMES") ?? "article-db-1,article-db-2")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var connTemplate = Environment.GetEnvironmentVariable("SHARD_CONN_TEMPLATE")
    ?? "Host={0};Port=5432;Database=articles;Username=hh;Password=hh";
var otlpEndpoint = Environment.GetEnvironmentVariable("OTLP_ENDPOINT") ?? "http://jaeger:4318";

var shards = shardNames
    .Select((name, i) => new Shard(i, name, string.Format(connTemplate, name)))
    .ToArray();

long requestCount = 0;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:4007");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Monitoring — distributed tracing exported to Jaeger over OTLP/HTTP.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("article-coordinator"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o =>
        {
            o.Protocol = OtlpExportProtocol.HttpProtobuf;
            o.Endpoint = new Uri(otlpEndpoint);
        }));

var app = builder.Build();

// Structured request log + counter, correlated to the active trace id.
app.Use(async (ctx, next) =>
{
    Interlocked.Increment(ref requestCount);
    var sw = Stopwatch.StartNew();
    await next();
    sw.Stop();
    var traceId = Activity.Current?.TraceId.ToString() ?? "n/a";
    Console.WriteLine(
        $"{{\"service\":\"article-coordinator\",\"traceId\":\"{traceId}\",\"method\":\"{ctx.Request.Method}\"," +
        $"\"path\":\"{ctx.Request.Path}\",\"status\":{ctx.Response.StatusCode},\"durationMs\":{sw.ElapsedMilliseconds}}}");
});

// Hash-modulo sharding. (A production system would use consistent hashing with
// virtual nodes so a shard-count change only remaps a fraction of the keys.)
Shard Route(int articleId) => shards[Math.Abs(articleId) % shards.Length];

app.MapGet("/health", () =>
    Results.Ok(new { status = "ok", service = "article-coordinator", shards = shards.Length }));

// GET /route/{articleId} -> the shard that owns this article (z-axis routing).
app.MapGet("/route/{articleId:int}", (int articleId) =>
{
    var shard = Route(articleId);
    return Results.Ok(new { articleId, shard = shard.Name, connection = shard.Connection });
});

// GET /shards -> the full topology (name + connection) for the ArticleService
// and the offline fill process.
app.MapGet("/shards", () =>
    Results.Ok(new { strategy = "z-axis (hash modulo shard count)", shards }));

app.MapGet("/metrics", () =>
{
    var requests = Interlocked.Read(ref requestCount);
    return Results.Text(
        "# HELP happyheadlines_http_requests_total Total HTTP requests served.\n" +
        "# TYPE happyheadlines_http_requests_total counter\n" +
        $"happyheadlines_http_requests_total{{service=\"article-coordinator\"}} {requests}\n",
        "text/plain; version=0.0.4");
});

app.Run();

internal record Shard(int Index, string Name, string Connection);
