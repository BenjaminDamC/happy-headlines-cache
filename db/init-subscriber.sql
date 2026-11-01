-- SubscriberDatabase — the SubscriberService's own database (its own "swimlane").
-- Deliberately separate from the article/comment databases so the subscriber
-- feature cannot take down (or be taken down by) the rest of HappyHeadlines.
CREATE TABLE subscribers (
  id         SERIAL PRIMARY KEY,
  email      TEXT NOT NULL UNIQUE,
  name       TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
