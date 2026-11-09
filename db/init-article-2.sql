-- ArticleDatabase SHARD 2 (article-db-2) — odd article ids.
-- The article database is scaled on the z-axis: articles are sharded by id
-- (hash modulo shard count). This shard holds the odd ids (id % 2 == 1 -> shard 1).
CREATE TABLE articles (
  id           INT PRIMARY KEY,
  title        TEXT NOT NULL,
  content      TEXT NOT NULL,
  published_at TIMESTAMPTZ NOT NULL
);

INSERT INTO articles (id, title, content, published_at) VALUES
  (1,  'City opens new tram line', 'The new north-south tram line opened today...', NOW() - interval '1 day'),
  (3,  'Local team wins cup final', 'A last-minute goal sealed the victory...', NOW() - interval '3 days'),
  (5,  'New library opens downtown', 'The renovated main library reopened with a digital lab...', NOW() - interval '5 days'),
  (7,  'Tech hub announces expansion', 'The company will hire 200 engineers...', NOW() - interval '9 days'),
  (9,  'Energy prices fall', 'Wholesale prices dropped sharply this week...', NOW() - interval '13 days'),
  (11, 'Election campaign kicks off', 'Parties launch their platforms ahead of the vote...', NOW() - interval '18 days');
