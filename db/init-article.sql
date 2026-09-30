-- ArticleDatabase seed: articles with published_at spread over the last ~3 weeks.
-- Articles older than 14 days are intentionally included so the offline
-- ArticleCache fill (latest 14 days) visibly does NOT cache them.
CREATE TABLE articles (
  id           SERIAL PRIMARY KEY,
  title        TEXT NOT NULL,
  content      TEXT NOT NULL,
  published_at TIMESTAMPTZ NOT NULL
);

INSERT INTO articles (title, content, published_at) VALUES
  ('City opens new tram line', 'The new north-south tram line opened today...', NOW() - interval '1 day'),
  ('Airport strike averted', 'Unions reached a deal hours before the deadline...', NOW() - interval '2 days'),
  ('Local team wins cup final', 'A last-minute goal sealed the victory...', NOW() - interval '3 days'),
  ('Housing prices keep climbing', 'Prices rose for the ninth month in a row...', NOW() - interval '4 days'),
  ('New library opens downtown', 'The renovated main library reopened with a digital lab...', NOW() - interval '5 days'),
  ('Flood warning issued', 'Heavy rain expected over the weekend...', NOW() - interval '7 days'),
  ('Tech hub announces expansion', 'The company will hire 200 engineers...', NOW() - interval '9 days'),
  ('Marathon draws record crowd', 'Over 30,000 runners took part...', NOW() - interval '11 days'),
  ('Energy prices fall', 'Wholesale prices dropped sharply this week...', NOW() - interval '13 days'),
  ('Museum night a hit', 'Tens of thousands visited museums after dark...', NOW() - interval '14 days'),
  ('Election campaign kicks off', 'Parties launch their platforms ahead of the vote...', NOW() - interval '18 days'),
  ('Old bridge to be replaced', 'The century-old bridge is slated for demolition...', NOW() - interval '25 days');
