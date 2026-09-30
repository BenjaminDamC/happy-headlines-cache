// ArticleCache OFFLINE fill — the background process that periodically copies the
// latest 14 days of articles from the database into Redis. This is the key
// difference from the CommentCache: the cache is pre-populated here, so the
// service never writes to it (a read of a non-cached article falls through to DB).
import { createClient } from 'redis';
import pg from 'pg';

const { Pool } = pg;
const REDIS_URL = process.env.REDIS_URL || 'redis://localhost:6379';
const DATABASE_URL = process.env.DATABASE_URL || 'postgres://hh:hh@localhost:5432/articles';
const INTERVAL_MS = parseInt(process.env.FILL_INTERVAL_MS || '30000', 10);

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

async function fill() {
  const { rows } = await pool.query(
    `SELECT id, title, content, published_at FROM articles
     WHERE published_at >= NOW() - interval '14 days'
     ORDER BY published_at DESC`
  );
  for (const a of rows) {
    await redis.set(`article:${a.id}`, JSON.stringify(a));
  }
  console.log(`[fill] cached ${rows.length} articles from the last 14 days`);
  return rows.length;
}

async function main() {
  await retry(() => redis.connect(), 'redis');
  await retry(() => pool.query('SELECT 1'), 'postgres');
  await fill();
  setInterval(fill, INTERVAL_MS);
}
main();
