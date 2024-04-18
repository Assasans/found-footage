DROP TABLE IF EXISTS votes;
CREATE TABLE IF NOT EXISTS votes (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  video_id UUID,
  user_id UUID,
  lobby_id UUID NULL,
  vote_type VARCHAR(16),
  flags INTEGER,
  ip VARCHAR(45) NULL,
  timestamp TIMESTAMP NULL
);

CREATE UNIQUE INDEX idx_votes_handle ON votes (video_id, user_id);
CREATE INDEX idx_votes_video ON votes (video_id);
CREATE INDEX idx_votes_user ON votes (user_id);
CREATE INDEX idx_votes_lobby ON votes (lobby_id);
CREATE INDEX idx_votes_vote_type ON votes (vote_type);
CREATE INDEX idx_votes_flags ON votes (flags);
CREATE INDEX idx_votes_ip ON votes (ip);

CREATE VIEW votes_flat AS
SELECT video_id, SUM(
  CASE
    WHEN vote_type = 'like' THEN 1
    WHEN vote_type = 'dislike' THEN -1
    ELSE 0
  END
) AS score
FROM votes
WHERE vote_type IN ('like', 'dislike') AND flags & 1 != 0
GROUP BY video_id;
