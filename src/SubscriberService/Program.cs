using System.Text;
using System.Text.Json;
using FeatureHubSDK;
using Npgsql;
using RabbitMQ.Client;

// SubscriberService — newsletter subscriber management (W44 "DevOps -> Recovery").
//
// Fault isolation: this service owns its OWN PostgreSQL database and only talks
// to the NewsletterService through RabbitMQ. A failure here cannot take the
// newsletter service down (and vice versa) — they sit in separate swimlanes.
//
// Release toggle: the whole service is hidden behind a FeatureHub boolean flag
// ("SubscriberService"). When the flag is OFF every endpoint returns 503, so the
// feature can be rolled back without a redeploy.

var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? "Host=localhost;Port=5432;Database=subscribers;Username=hh;Password=hh";
var rabbitHost = Environment.GetEnvironmentVariable("RABBITMQ_URL") ?? "rabbitmq";
var featurehubUrl = Environment.GetEnvironmentVariable("FEATUREHUB_URL") ?? "http://featurehub:8085";
var featurehubKey = Environment.GetEnvironmentVariable("FEATUREHUB_API_KEY") ?? "default/<sdk-key>";

const string Exchange = "happyheadlines";
const string Queue = "subscriber.events";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:4004");
builder.Services.AddCors();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

// --- Release toggle (FeatureHub). Fail closed: if unreachable, the feature is OFF. ---
var featurehub = await ConnectFeatureHubAsync(featurehubUrl, featurehubKey);

// --- Queue (RabbitMQ) + database (PostgreSQL), with startup retries. ---
var channel = ConnectRabbit(rabbitHost);
await using var dataSource = NpgsqlDataSource.Create(databaseUrl);
await WithRetryAsync(async () =>
{
    await using var cmd = dataSource.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync();
}, "postgres");

bool Enabled() => featurehub is not null && featurehub["SubscriberService"].IsEnabled;

app.MapGet("/health", () =>
    Results.Ok(new
    {
        status = "ok",
        service = "subscriber-service",
        releaseToggle = Enabled() ? "on" : "off"
    }));

app.MapGet("/subscribers", async () =>
{
    if (!Enabled()) return Results.Json(new { error = "subscriber service disabled" }, statusCode: 503);

    await using var cmd = dataSource.CreateCommand(
        "SELECT id, email, name, created_at FROM subscribers ORDER BY created_at");
    await using var reader = await cmd.ExecuteReaderAsync();

    var subscribers = new List<object>();
    while (await reader.ReadAsync())
    {
        subscribers.Add(new
        {
            id = reader.GetInt32(0),
            email = reader.GetString(1),
            name = reader.IsDBNull(2) ? null : reader.GetString(2),
            createdAt = reader.GetFieldValue<DateTimeOffset>(3),
        });
    }
    return Results.Ok(new { subscribers });
});

app.MapPost("/subscribers", async (SubscriberRequest req) =>
{
    if (!Enabled()) return Results.Json(new { error = "subscriber service disabled" }, statusCode: 503);

    if (string.IsNullOrWhiteSpace(req.Email))
        return Results.BadRequest(new { error = "email is required" });

    // Upsert + capture whether this is a new or returning subscriber.
    await using var cmd = dataSource.CreateCommand(
        @"INSERT INTO subscribers (email, name)
          VALUES ($1, $2)
          ON CONFLICT (email) DO UPDATE SET name = EXCLUDED.name
          RETURNING id, (xmax = 0) AS created");
    cmd.Parameters.AddWithValue(req.Email.Trim().ToLowerInvariant());
    cmd.Parameters.AddWithValue((object?)req.Name ?? DBNull.Value);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    var id = reader.GetInt32(0);
    var created = reader.GetBoolean(1);

    Publish(channel, created ? "subscriber.created" : "subscriber.updated", req.Email.Trim().ToLowerInvariant());

    return Results.Ok(new { id, email = req.Email.Trim().ToLowerInvariant(), created });
});

app.MapDelete("/subscribers/{email}", async (string email) =>
{
    if (!Enabled()) return Results.Json(new { error = "subscriber service disabled" }, statusCode: 503);

    var normalized = email.Trim().ToLowerInvariant();
    await using var cmd = dataSource.CreateCommand("DELETE FROM subscribers WHERE email = $1");
    cmd.Parameters.AddWithValue(normalized);
    var deleted = await cmd.ExecuteNonQueryAsync();

    if (deleted == 0) return Results.NotFound(new { error = "not found" });

    Publish(channel, "subscriber.deleted", normalized);
    return Results.Ok(new { email = normalized, deleted = true });
});

app.Run();

static void Publish(IModel channel, string eventType, string email)
{
    var payload = JsonSerializer.Serialize(new
    {
        @event = eventType,
        email,
        timestamp = DateTimeOffset.UtcNow
    });
    var body = Encoding.UTF8.GetBytes(payload);
    channel.BasicPublish(Exchange, routingKey: eventType, basicProperties: null, body);
    Console.WriteLine($"[subscriber-service] published {eventType} for {email}");
}

static async Task<IClientContext?> ConnectFeatureHubAsync(string url, string key)
{
    try
    {
        var config = new EdgeFeatureHubConfig(url, key);
        await config.Init();
        return await config.NewContext().Build();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[featurehub] unavailable, release toggle defaults to OFF: {ex.Message}");
        return null;
    }
}

static IModel ConnectRabbit(string host, int tries = 40)
{
    var factory = new ConnectionFactory { HostName = host };
    for (var i = 0; i < tries; i++)
    {
        try
        {
            var connection = factory.CreateConnection();
            var channel = connection.CreateModel();
            channel.ExchangeDeclare(Exchange, ExchangeType.Topic, durable: true);
            channel.QueueDeclare(Queue, durable: true, exclusive: false, autoDelete: false);
            channel.QueueBind(Queue, Exchange, routingKey: "subscriber.*");
            Console.WriteLine("rabbitmq connected");
            return channel;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[rabbitmq] not ready: {ex.Message}");
            Thread.Sleep(1000);
        }
    }
    throw new Exception("rabbitmq never connected");
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

internal record SubscriberRequest(string Email, string? Name);
