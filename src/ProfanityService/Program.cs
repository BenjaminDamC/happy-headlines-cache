using System.Diagnostics;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ProfanityService — screens comment text against a small profanity word list.
//
// It lives in its OWN swimlane: the CommentService calls it over HTTP. When this
// service is unavailable, the CommentService's circuit breaker isolates the
// failure and the CommentService keeps working (comments are accepted but left
// unmoderated) instead of cascading the outage.

var otlpEndpoint = Environment.GetEnvironmentVariable("OTLP_ENDPOINT") ?? "http://jaeger:4318";

long requestCount = 0;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:4006");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// Monitoring — distributed tracing exported to Jaeger over OTLP/HTTP.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("profanity-service"))
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
        $"{{\"service\":\"profanity-service\",\"traceId\":\"{traceId}\",\"method\":\"{ctx.Request.Method}\"," +
        $"\"path\":\"{ctx.Request.Path}\",\"status\":{ctx.Response.StatusCode},\"durationMs\":{sw.ElapsedMilliseconds}}}");
});

var profanities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "damn", "hell", "crap", "bastard", "bitch", "shit", "fuck", "asshole"
};

app.MapGet("/health", () =>
    Results.Ok(new { status = "ok", service = "profanity-service" }));

// GET /check?text=<content> -> { flagged, matches }
app.MapGet("/check", (string text) =>
{
    if (string.IsNullOrWhiteSpace(text))
        return Results.Ok(new { flagged = false, matches = Array.Empty<string>() });

    var matches = text
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.Trim('.', ',', '!', '?', ':', ';', '"', '\'', ')', '('))
        .Where(profanities.Contains)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return Results.Ok(new { flagged = matches.Length > 0, matches });
});

app.MapGet("/metrics", () =>
{
    var requests = Interlocked.Read(ref requestCount);
    return Results.Text(
        "# HELP happyheadlines_http_requests_total Total HTTP requests served.\n" +
        "# TYPE happyheadlines_http_requests_total counter\n" +
        $"happyheadlines_http_requests_total{{service=\"profanity-service\"}} {requests}\n",
        "text/plain; version=0.0.4");
});

app.Run();
