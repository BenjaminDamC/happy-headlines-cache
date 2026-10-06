// ArticleService — reads articles through the ArticleCache (Redis).
// The cache is filled OFFLINE (see fill-cache.js), never on-demand: this service
// only reads Redis and falls back to the database on a miss (e.g. an article
// outside the 14-day window).
import http from 'node:http';
import { createClient } from 'redis';
import pg from 'pg';

const { Pool } = pg;
const REDIS_URL = process.env.REDIS_URL || 'redis://localhost:6379';
const DATABASE_URL = process.env.DATABASE_URL || 'postgres://hh:hh@localhost:5432/articles';

const redis = createClient({ url: REDIS_URL });
redis.on('error', (e) => console.error('[redis]', e.message));
const pool = new Pool({ connectionString: DATABASE_URL });

// Cache hit-ratio counters (in-memory; reset on restart).
let cacheHits = 0;
let cacheMisses = 0;

async function retry(fn, label, tries = 40) {
  for (let i = 0; i < tries; i++) {
    try { await fn(); console.log(`${label} connected`); return; }
    catch { await new Promise(r => setTimeout(r, 1000)); }
  }
  throw new Error(`${label} never connected`);
}

const json = (res, code, obj) => {
  res.writeHead(code, {
    'Content-Type': 'application/json',
    'Access-Control-Allow-Origin': '*',
  });
  res.end(JSON.stringify(obj));
};

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');

  if (url.pathname === '/health') {
    return json(res, 200, { status: 'ok', service: 'article-service', cache: REDIS_URL });
  }

  if (url.pathname === '/cache/size') {
    const keys = await redis.keys('article:*');
    return json(res, 200, { cachedArticles: keys.length });
  }

  if (url.pathname === '/cache/hit-ratio') {
    const total = cacheHits + cacheMisses;
    const hitRatio = total ? cacheHits / total : 0;
    return json(res, 200, { service: 'article-service', hits: cacheHits, misses: cacheMisses, total, hitRatio });
  }

  if (url.pathname === '/articles') {
    const { rows } = await pool.query('SELECT id, title, published_at FROM articles ORDER BY published_at DESC');
    const cached = new Set((await redis.keys('article:*')).map(k => k.split(':')[1]));
    const articles = rows.map(a => ({ ...a, cached: cached.has(String(a.id)) }));
    return json(res, 200, { articles });
  }

  const m = url.pathname.match(/^\/articles\/(\d+)$/);
  if (m) {
    const id = m[1];
    const cached = await redis.get(`article:${id}`);
    if (cached) {
      cacheHits++;
      return json(res, 200, { source: 'cache', article: JSON.parse(cached) });
    }

    const { rows } = await pool.query('SELECT id, title, content, published_at FROM articles WHERE id = $1', [id]);
    if (!rows[0]) return json(res, 404, { error: 'not found' });
    cacheMisses++;
    return json(res, 200, { source: 'db', article: rows[0] });
  }

  json(res, 404, { error: 'not found' });
});

async function main() {
  await retry(() => redis.connect(), 'redis');
  await retry(() => pool.query('SELECT 1'), 'postgres');
  server.listen(4001, () => console.log('ArticleService listening on 4001'));
}
main();
