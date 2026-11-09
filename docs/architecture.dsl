workspace "HappyHeadlines" "DLS Compulsory Assignment #2 (W46): a scaled, fault-isolated, observable news platform." {

    model {
        rider  = person "Rider" "Reads news and comments"
        editor = person "Editor" "Publishes articles and moderates comments"

        hh = softwareSystem "HappyHeadlines" "News platform" {

            web = container "Web App" "React single-page app" "JavaScript"

            # --- Article flow: x-axis replicas behind an LB, z-axis sharded DB ---
            articleLb = container "Article Load Balancer" "nginx. Distributes requests across the ArticleService replicas (x-axis scaling)." "nginx"
            articleSvc = container "ArticleService" "Serves articles. Replicated behind the load balancer." "C# / .NET 9" {
                api = component "HTTP API" "Minimal API endpoints (articles, health, cache stats, metrics)" "ASP.NET Core"
                cacheReader = component "Cache Reader" "Reads articles from Redis; falls back to a shard on a miss" "StackExchange.Redis"
                coordinatorClient = component "Coordinator Client" "Resolves an article id to its database shard" "HttpClient"
                shardReaders = component "Shard Readers" "One Npgsql data source per article database shard" "Npgsql"
                telemetry = component "Telemetry" "OTLP tracing, Prometheus metrics, structured logging" "OpenTelemetry"
            }
            articleCoordinator = container "ArticleCoordinator" "Routes an article id to its database shard (z-axis coordination component)." "C# / .NET 9"

            articleCache = container "ArticleCache" "Redis. The offline fill process pre-populates the latest 14 days." "Redis"
            articleDb1 = container "ArticleDatabase Shard 1" "PostgreSQL. Even article ids (z-axis shard)." "PostgreSQL"
            articleDb2 = container "ArticleDatabase Shard 2" "PostgreSQL. Odd article ids (z-axis shard)." "PostgreSQL"

            # --- Comment flow ---
            commentSvc = container "CommentService" "Serves and moderates comments; calls ProfanityService through a circuit breaker." "C# / .NET 9"
            profanitySvc = container "ProfanityService" "Screens comment text for profanity. Its own swimlane." "C# / .NET 9"

            commentCache = container "CommentCache" "Redis. Cache-miss fill; LRU eviction (30 articles)." "Redis"
            commentDb = container "CommentDatabase" "PostgreSQL. Global instance in North America." "PostgreSQL"

            # --- Subscriber / newsletter flow (W44) ---
            subscriberSvc = container "SubscriberService" "Owns subscriber data; hidden behind a FeatureHub release toggle." "C# / .NET 9"
            newsletterSvc = container "NewsletterService" "Consumes subscriber events and also reads the release toggle." "C# / .NET 9"
            rabbit = container "RabbitMQ" "Queue that decouples the subscriber and newsletter swimlanes." "RabbitMQ"
            featurehub = container "FeatureHub" "Release-toggle backend." "FeatureHub"

            # --- Monitoring ---
            jaeger = container "Jaeger" "Distributed tracing UI (receives OTLP)." "Jaeger"
            prometheus = container "Prometheus" "Scrapes each service's /metrics endpoint." "Prometheus"
        }

        # --- Relationships (level 2) ---
        rider  -> web        "reads news"
        editor -> web        "publishes articles"

        web -> articleLb     "HTTPS"
        web -> commentSvc    "HTTPS"
        web -> subscriberSvc "HTTPS"

        articleLb -> articleSvc "round-robin (x-axis)"

        articleSvc -> articleCache      "read-through cache"
        articleSvc -> articleCoordinator "GET /route/{id} (z-axis)"
        articleCoordinator -> articleDb1 "route"
        articleCoordinator -> articleDb2 "route"
        articleSvc -> articleDb1       "read on cache miss"
        articleSvc -> articleDb2       "read on cache miss"

        commentSvc -> commentCache      "cache-miss + LRU"
        commentSvc -> commentDb         "read/write comments"
        commentSvc -> profanitySvc      "moderate via circuit breaker"

        subscriberSvc -> rabbit         "publish subscriber.* events"
        subscriberSvc -> featurehub     "release toggle"
        newsletterSvc -> rabbit         "consume subscriber.* events"
        newsletterSvc -> featurehub     "release toggle"

        articleSvc -> jaeger           "OTLP traces"
        commentSvc -> jaeger           "OTLP traces"
        profanitySvc -> jaeger         "OTLP traces"
        articleCoordinator -> jaeger   "OTLP traces"
        prometheus -> articleSvc       "scrape /metrics"
        prometheus -> commentSvc       "scrape /metrics"
        prometheus -> profanitySvc     "scrape /metrics"
        prometheus -> articleCoordinator "scrape /metrics"

        # --- Relationships (level 3, inside ArticleService) ---
        api -> cacheReader          "on article read"
        cacheReader -> coordinatorClient "on cache miss"
        coordinatorClient -> articleCoordinator "GET /route/{id}"
        cacheReader -> shardReaders "read article from the resolved shard"
        api -> telemetry            "span + metrics per request"
    }

    views {
        systemContext hh "System Context" {
            include *
        }

        container hh "Containers (level 2)" {
            include *
        }

        component articleSvc "ArticleService (level 3)" {
            include *
        }
    }
}
