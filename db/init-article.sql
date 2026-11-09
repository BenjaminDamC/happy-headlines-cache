-- ArticleDatabase SHARD 1 (article-db-1) — even article ids.
-- The article database is scaled on the z-axis: articles are sharded by id
-- (hash modulo shard count). This shard holds the even ids (id % 2 == 0 -> shard 0).
-- Articles older than 14 days are intentionally included so the offline
-- ArticleCache fill (latest 14 days) visibly does NOT cache them.
CREATE TABLE articles (
  id           INT PRIMARY KEY,
  title        TEXT NOT NULL,
  content      TEXT NOT NULL,
  published_at TIMESTAMPTZ NOT NULL
);

INSERT INTO articles (id, title, content, published_at) VALUES
  (2,  'Airport strike averted', 'Unions reached a deal hours before the deadline...', NOW() - interval '2 days'),
  (4,  'Housing prices keep climbing', 'Prices rose for the ninth month in a row...', NOW() - interval '4 days'),
  (6,  'Flood warning issued', 'Heavy rain expected over the weekend...', NOW() - interval '7 days'),
  (8,  'Marathon draws record crowd', 'Over 30,000 runners took part...', NOW() - interval '11 days'),
  (10, 'Museum night a hit', 'Tens of thousands visited museums after dark...', NOW() - interval '14 days'),
  (12, 'Old bridge to be replaced', 'The century-old bridge is slated for demolition...', NOW() - interval '25 days');
