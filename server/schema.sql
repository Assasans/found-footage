DROP TABLE IF EXISTS videos;
CREATE TABLE IF NOT EXISTS videos (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  video_id UUID,
  user_id UUID,
  language VARCHAR(16),
  reason VARCHAR(32),
  object TEXT UNIQUE,
  available BOOLEAN,
  ip VARCHAR(45) NULL,
  timestamp TIMESTAMP NULL
);

CREATE UNIQUE INDEX idx_videos_video ON videos (video_id);
CREATE INDEX idx_videos_user ON videos (user_id);
CREATE INDEX idx_videos_language ON videos (language);
CREATE INDEX idx_videos_reason ON videos (reason);
CREATE INDEX idx_videos_available ON videos (available);
CREATE INDEX idx_videos_ip ON videos (ip);
