using System.Text.Json;

// ProfanityService — screens comment text against a small profanity word list.
//
// It lives in its OWN swimlane: the CommentService calls it over HTTP. When this
// service is unavailable, the CommentService's circuit breaker isolates the
// failure and the CommentService keeps working (comments are accepted but left
// unmoderated) instead of cascading the outage.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:4006");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

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

app.Run();
