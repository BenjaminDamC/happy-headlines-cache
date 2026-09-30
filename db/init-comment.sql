-- CommentDatabase seed: comments attached to articles (referenced by article id,
-- which matches the ArticleDatabase ids by convention in this demo).
CREATE TABLE comments (
  id         SERIAL PRIMARY KEY,
  article_id INT NOT NULL,
  author     TEXT NOT NULL,
  content    TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO comments (article_id, author, content) VALUES
  (1, 'Alice', 'Finally! Been waiting for this line for years.'),
  (1, 'Bob', 'Great, but will it run on time?'),
  (2, 'Carla', 'Relieved. I fly out this weekend.'),
  (3, 'Dan', 'What a match. Still can not believe it.'),
  (3, 'Eva', 'Deserved win after that season.'),
  (4, 'Frank', 'Rents are getting out of hand.'),
  (5, 'Grace', 'The digital lab is a great addition.'),
  (6, 'Heidi', 'Stay safe everyone.'),
  (7, 'Ivan', 'More jobs is always good news.'),
  (8, 'Julia', 'The atmosphere was amazing.'),
  (9, 'Karl', 'Finally some relief on the bill.'),
  (10, 'Lena', 'Already looking forward to next year.');
