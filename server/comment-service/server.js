// CommentService — cache-miss with LRU eviction.
// Reads comments through the CommentCache (Redis). On a MISS it fetches from the
// database AND stores the result, tracking the most-recently-accessed articles in
// a sorted set. When more than 30 articles are cached, the least-recently-used one
// is evicted.
import http from 'node:http';
import { createClient } from 'redis';
import pg from 'pg';

const { Pool } = pg;
const MAX_CACHED_ARTICLES = 30;
const LRU_KEY = 'commentcache:lru';
const REDIS_URL = process.env.REDIS_URL || 'redis://localhost:6379';
const DATABASE_URL = process.env.DATABASE_URL || 'postgres://hh:hh@localhost:5432/comments';

const redis = createClient({ url: REDIS_URL });
redis.on('error', (e) => console.error('[redis]', e.message));
const pool = new Pool({ connectionString: DATABASE_URL });

async function retry(fn, label, tries = 40) {
  for (let i = 0; i < tries; i++) {
    try { await fn(); console.log(`${label} connected`); return; }
    catch { await new Promise(r => setTimeout(r, 1000)); }
  }
  throw new Error(`${label} never connected`);
}

const json = (res, code, obj) => {
  res.writeHead(code, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(obj));
};

async function getCommentsFromDb(articleId) {
  const { rows } = await pool.query(
    'SELECT id, article_id, author, content, created_at FROM comments WHERE article_id = $1 ORDER BY created_at',
    [articleId]
  );
  return rows;
}

async function evictIfOver() {
  const count = await redis.zCard(LRU_KEY);
  if (count > MAX_CACHED_ARTICLES) {
    const oldest = await redis.zRange(LRU_KEY, 0, 0);
    if (oldest.length) {
      await redis.del(`comments:${oldest[0]}`);
      await redis.zRem(LRU_KEY, oldest[0]);
      console.log(`[lru] evicted article ${oldest[0]} (${count - 1}/${MAX_CACHED_ARTICLES} kept)`);
    }
  }
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');

  if (url.pathname === '/health') {
    return json(res, 200, { status: 'ok', service: 'comment-service', cache: REDIS_URL });
  }

  if (url.pathname === '/cache/stats') {
    const cachedArticles = await redis.zCard(LRU_KEY);
    return json(res, 200, { cachedArticles, max: MAX_CACHED_ARTICLES });
  }

  const m = url.pathname.match(/^\/articles\/(\d+)\/comments$/);
  if (m) {
    const articleId = m[1];
    const cached = await redis.get(`comments:${articleId}`);
    if (cached) {
      await redis.zAdd(LRU_KEY, { score: Date.now(), value: articleId }); // touch → becomes most-recent
      return json(res, 200, { source: 'cache', comments: JSON.parse(cached) });
    }

    const comments = await getCommentsFromDb(articleId);
    await redis.set(`comments:${articleId}`, JSON.stringify(comments));
    await redis.zAdd(LRU_KEY, { score: Date.now(), value: articleId });
    await evictIfOver();
    return json(res, 200, { source: 'db', comments });
  }

  json(res, 404, { error: 'not found' });
});

async function main() {
  await retry(() => redis.connect(), 'redis');
  await retry(() => pool.query('SELECT 1'), 'postgres');
  server.listen(4002, () => console.log('CommentService listening on 4002'));
}
main();
