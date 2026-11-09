using System.Text;
using System.Text.Json;
using FeatureHubSDK;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

// NewsletterService — the consumer in the second swimlane.
//
// It has NO database of its own for subscriber data: it only listens to the
// subscriber events published by the SubscriberService over RabbitMQ and reacts
// (here: logs the "email" it would send). If the SubscriberService is down, the
// events simply queue up — this is the fault isolation between the two services.
//
// Release toggle: the whole SubscriberService feature is hidden behind a
// FeatureHub boolean flag ("SubscriberService"). The SubscriberService itself
// refuses requests when the flag is OFF; this service ALSO reads the flag and
// decides what to do — when OFF it skips sending and just acknowledges the
// queued events, so the rollback is consistent across both swimlanes.

var rabbitHost = Environment.GetEnvironmentVariable("RABBITMQ_URL") ?? "rabbitmq";
var featurehubUrl = Environment.GetEnvironmentVariable("FEATUREHUB_URL") ?? "http://featurehub:8085";
var featurehubKey = Environment.GetEnvironmentVariable("FEATUREHUB_API_KEY") ?? "default/<sdk-key>";

const string Exchange = "happyheadlines";
const string Queue = "subscriber.events";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:4005");
builder.Services.AddCors();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

// --- Release toggle (FeatureHub). Fail closed: if unreachable, treat as OFF. ---
var featurehub = await ConnectFeatureHubAsync(featurehubUrl, featurehubKey);

var channel = ConnectRabbit(rabbitHost);

bool SubscriberFeatureEnabled() =>
    featurehub is not null && featurehub["SubscriberService"].IsEnabled;

// Subscribe to subscriber events and "send" the corresponding email.
var consumer = new EventingBasicConsumer(channel);
consumer.Received += (_, ea) =>
{
    var text = Encoding.UTF8.GetString(ea.Body.ToArray());
    var evt = JsonSerializer.Deserialize<SubscriberEvent>(text, JsonDefaults.Web);
    if (evt is null) return;

    // Release toggle: when the SubscriberService feature is OFF, skip sending
    // (the feature is rolled back) — the message is still acknowledged.
    if (!SubscriberFeatureEnabled())
    {
        Console.WriteLine($"[newsletter-service] SubscriberService toggle OFF — skipping {evt.Event} for {evt.Email}");
        return;
    }

    var action = evt.Event switch
    {
        "subscriber.created" => "welcome email",
        "subscriber.updated" => "confirmation email",
        "subscriber.deleted" => "goodbye email",
        _ => "ignored event"
    };
    Console.WriteLine($"[newsletter-service] {action} -> {evt.Email} (at {evt.Timestamp:O})");
};
channel.BasicConsume(Queue, autoAck: true, consumer: consumer);

app.MapGet("/health", () =>
    Results.Ok(new
    {
        status = "ok",
        service = "newsletter-service",
        queue = Queue,
        subscriberFeature = SubscriberFeatureEnabled() ? "on" : "off",
    }));

app.Run();

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

internal record SubscriberEvent(string Event, string Email, DateTimeOffset Timestamp);

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
