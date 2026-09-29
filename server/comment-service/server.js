// CommentService — stub for the W40 cache-layer assignment (week-1 scope).
// The cache integration is deliberately left as a TODO for a later week; this
// stub exists so the Docker Compose topology is runnable end-to-end.
const http = require('node:http');

const PORT = process.env.PORT || 4002;
const CACHE_HOST = process.env.COMMENT_CACHE_HOST || 'comment-cache';

const server = http.createServer((req, res) => {
  if (req.url === '/health') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ status: 'ok', service: 'comment-service', cache: CACHE_HOST }));
    return;
  }
  if (req.url === '/comments') {
    // TODO (later week): read-through the CommentCache (Redis), cache-miss style.
    //  - On hit: return cached comments.
    //  - On miss: read from CommentDatabase, store comments for this article in the
    //    cache. The cache is limited to the 30 most recently accessed articles and
    //    evicts the least-recently-used (LRU) when full.
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ service: 'comment-service', note: 'cache integration TODO' }));
    return;
  }
  res.writeHead(404);
  res.end('not found');
});

server.listen(PORT, () => console.log(`CommentService listening on :${PORT}`));
