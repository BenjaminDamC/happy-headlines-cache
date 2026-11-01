using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

// NewsletterService — the consumer in the second swimlane.
//
// It has NO database of its own for subscriber data: it only listens to the
// subscriber events published by the SubscriberService over RabbitMQ and reacts
// (here: logs the "email" it would send). If the SubscriberService is down, the
// events simply queue up — this is the fault isolation between the two services.

var rabbitHost = Environment.GetEnvironmentVariable("RABBITMQ_URL") ?? "rabbitmq";

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

var channel = ConnectRabbit(rabbitHost);

// Subscribe to subscriber events and "send" the corresponding email.
var consumer = new EventingBasicConsumer(channel);
consumer.Received += (_, ea) =>
{
    var text = Encoding.UTF8.GetString(ea.Body.ToArray());
    var evt = JsonSerializer.Deserialize<SubscriberEvent>(text);
    if (evt is null) return;

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
    Results.Ok(new { status = "ok", service = "newsletter-service", queue = Queue }));

app.Run();

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
