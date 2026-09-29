workspace "HappyHeadlines — Cache Layers" "DLS Compulsory Assignment #1 (W40): availability fix via cache layers between the region-replicated services and the North-America-only global databases." {

    model {
        rider  = person "Rider" "Reads news and comments"
        editor = person "Editor" "Publishes articles"

        hh = softwareSystem "HappyHeadlines" "News platform" {

            web = container "Web App" "React single-page app"

            articleSvc = container "ArticleService" "Serves articles. Already replicated to each region." "Node.js"
            commentSvc = container "CommentService" "Serves comments. Already replicated to each region." "Node.js"

            articleCache = container "ArticleCache" "Redis. Offline process fills it with articles from the latest 14 days." "Redis"
            commentCache = container "CommentCache" "Redis. Cache-miss fill; holds comments for the 30 most recently accessed articles; LRU eviction." "Redis"

            articleDb = container "ArticleDatabase" "PostgreSQL. Single global instance in North America." "PostgreSQL"
            commentDb  = container "CommentDatabase" "PostgreSQL. Single global instance in North America." "PostgreSQL"
        }

        rider  -> web        "reads news"
        editor -> web        "publishes articles"
        web    -> articleSvc "HTTPS"
        web    -> commentSvc "HTTPS"

        # The cache layers sit between the region-local services and the
        # distant North-America databases — this is the whole fix.
        articleSvc -> articleCache "read-through cache"
        articleSvc -> articleDb    "on cache miss"
        commentSvc -> commentCache "read-through cache"
        commentSvc -> commentDb    "on cache miss"
    }

    views {
        systemContext hh "System Context" {
            include *
        }
        container hh "Containers (cache layers highlighted)" {
            include *
        }
    }
}
