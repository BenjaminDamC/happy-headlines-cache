// ArticleService — stub for the W40 cache-layer assignment (week-1 scope).
// The cache integration is deliberately left as a TODO for a later week; this
// stub exists so the Docker Compose topology is runnable end-to-end.
const http = require('node:http');

const PORT = process.env.PORT || 4001;
const CACHE_HOST = process.env.ARTICLE_CACHE_HOST || 'article-cache';

const server = http.createServer((req, res) => {
  if (req.url === '/health') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ status: 'ok', service: 'article-service', cache: CACHE_HOST }));
    return;
  }
  if (req.url === '/articles') {
    // TODO (later week): read-through the ArticleCache (Redis).
    //  - On hit: return the cached article.
    //  - On miss: read from ArticleDatabase, and an offline process periodically
    //    fills the cache with articles from the latest 14 days.
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ service: 'article-service', note: 'cache integration TODO' }));
    return;
  }
  res.writeHead(404);
  res.end('not found');
});

server.listen(PORT, () => console.log(`ArticleService listening on :${PORT}`));
