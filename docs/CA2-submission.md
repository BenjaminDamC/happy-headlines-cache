# DLS CA#2 — "Summarising Happy Headlines" submission guide

This maps every field in the Moodle submission to the code/doc you should
paste, plus the short explanation the brief asks for. Repo (public):
`https://github.com/BenjaminDamC/happy-headlines-cache`

---

## GitHub repository

```
https://github.com/BenjaminDamC/happy-headlines-cache
```

## C4 diagrams

**Level 2 (containers)** — render `docs/architecture.dsl` (open in Structurizr
Lite, or use the Mermaid diagram at the top of the README) and screenshot the
container view. It shows every container: the article load balancer, the two
ArticleService replicas, the ArticleCoordinator, the two article DB shards,
CommentService + ProfanityService, the subscriber/newsletter swimlanes (RabbitMQ
+ FeatureHub) and the monitoring stack (Jaeger + Prometheus).

**Level 3 (components, ArticleService)** — the `component articleSvc` view in
`docs/architecture.dsl`. ArticleService is decomposed into: the HTTP API, the
Cache Reader, the Coordinator Client (asks the coordinator which shard owns an
article), the Shard Readers (one Npgsql source per shard) and Telemetry.

## The scale cube

**z-axis — coordination component.** The `ArticleCoordinator`
(`src/ArticleCoordinator/Program.cs`) owns the shard routing:

```csharp
Shard Route(int articleId) => shards[Math.Abs(articleId) % shards.Length];
```

`GET /route/{articleId}` returns which shard owns an article; `GET /shards`
returns the full topology. The ArticleService asks it on every cache miss and
never hard-codes the shard layout.

**x-axis — load balancer.** `deploy/nginx.conf`:

```nginx
upstream article-service {
    server article-service:4001;
    server article-service-2:4001;
}
```

**How the load balancer works for this split:** the two ArticleService replicas
are identical and stateless (they only read; the cache is filled offline), so a
request can go to either one. nginx round-robins — request 1 → replica A,
request 2 → replica B, and so on — spreading load evenly. Because the LB is the
only entry point, replicas can be added or removed without the client knowing.
(The z-axis sharding is independent: *which* shard a request reads is decided by
the coordinator, not the LB — the LB only picks *which replica* handles it.)

## Fault isolation (circuit breaker)

`src/CommentService/CircuitBreaker.cs` is the hand-rolled breaker; the call is in
the `POST /articles/{id}/comments` handler in `src/CommentService/Program.cs`.

**How swimlanes relate to this code:** CommentService and ProfanityService sit in
different swimlanes — separate deployables with no shared state, talking only
over HTTP. If ProfanityService dies, the breaker opens after 3 failures and the
CommentService *degrades* (accepts the comment unmoderated) instead of failing
every comment. That's the point of a swimlane: one lane's failure stays in its
lane. The breaker is the seam that enforces it.

## Design to be monitored

- **Tracing:** OpenTelemetry in `ArticleService`, `ArticleCoordinator`,
  `CommentService`, `ProfanityService`, exported OTLP → Jaeger
  (http://localhost:16686). Post a comment and you'll see one trace spanning
  `comment-service` → `profanity-service`.
- **Metrics:** each service exposes `/metrics` (Prometheus text format);
  Prometheus (http://localhost:9090) scrapes them. Includes
  `happyheadlines_http_requests_total`, `_cache_hits_total`,
  `_cache_misses_total` and `happyheadlines_circuit_breaker_state`.
- **Logging:** structured JSON log lines with the trace id, e.g.
  `{"service":"comment-service","traceId":"...","method":"POST","path":"/articles/1/comments","status":200,"durationMs":12}`.

Screenshot each (Jaeger trace view, Prometheus graph, a log line) and describe
what it shows.

## DevOps

**CI/CD:** `.github/workflows/ci-cd.yml` builds every service and pushes the
image to GHCR (tagged `latest` + git SHA). CI part = build/test; CD part = push
to the registry.

**Feature toggle:** the whole SubscriberService is behind the FeatureHub flag
`SubscriberService`. The code that *decides what to do* lives in
`src/NewsletterService/Program.cs`:

```csharp
if (!SubscriberFeatureEnabled())   // toggle OFF
{
    Console.WriteLine($"[newsletter-service] SubscriberService toggle OFF — skipping {evt.Event} for {evt.Email}");
    return;                        // ack but don't send
}
```

## Sustainability (GAF tactics)

Three tactics, documented with code in `docs/sustainability.md`:

1. **Compression** — gzip responses on ArticleService + CommentService.
2. **Write-if-changed** — `article-fill` skips Redis writes when nothing changed.
3. **Response caching** — `Cache-Control: max-age=60` on article reads.

For each, screenshot the code (or a before/after diagram) and state the tactic.

## Putting it all together (≤5 min video)

Suggested tour:
1. Repo + running `docker compose up`.
2. C4 diagrams (30s).
3. Scale cube: `GET /route/1` vs `/route/2` (z-axis), LB round-robin (x-axis) (60s).
4. Circuit breaker: kill ProfanityService, post comments, watch `/circuit` open (60s).
5. Monitoring: Jaeger trace + Prometheus graph (60s).
6. Feature toggle: flip `SubscriberService`, watch NewsletterService skip (45s).
7. Sustainability: the three GAF tactics (45s).
