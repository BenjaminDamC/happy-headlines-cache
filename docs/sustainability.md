# Sustainability — GAF tactics (W45)

Three concrete **GAF (Green Architecture Framework) tactics** applied to
HappyHeadlines, each reducing the energy / resources the system needs to serve
the same workload. Each is a real code change (not just a slide).

## 1. Response compression — reduce data on the wire

**Tactic:** compression. The less data we send, the less energy every request
costs (network, NIC, serialisation).

`ArticleService` and `CommentService` enable ASP.NET Core response compression,
so article and comment payloads are gzip-compressed before leaving the service.

```csharp
builder.Services.AddResponseCompression();   // ArticleService / CommentService
// ...
app.UseResponseCompression();
```

A ~40 kB JSON article list compresses to a few kB, cutting bandwidth — and the
energy to move it — by an order of magnitude.

## 2. Write-if-changed — eliminate redundant writes

**Tactic:** reduce redundant work. The `article-fill` process runs on a timer and
used to rewrite every article into Redis on every tick, even when nothing had
changed. Now it compares against the cached value and skips identical writes:

```csharp
var serialized = JsonSerializer.Serialize(article, json);
var existing = await db.StringGetAsync($"article:{article.Id}");
if (!existing.IsNull && existing.ToString() == serialized)
{
    skipped++;            // nothing changed — no write
    continue;
}
await db.StringSetAsync($"article:{article.Id}", serialized);
```

On an idle news feed the fill does **zero** Redis writes per tick instead of 12.

## 3. HTTP response caching — serve repeat reads locally

**Tactic:** caching. Article reads set a `Cache-Control` header so clients (and
any intermediary) reuse the response for a minute instead of re-hitting the
network, the cache and the database.

```csharp
app.MapGet("/articles/{id:int}", async (int id, HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "public, max-age=60";
    // ...
});
```

Hot articles are served from the client's local cache, removing entire network
round-trips and their associated energy for the most popular reads.
