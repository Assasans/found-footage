ALTER TABLE videos ADD version VARCHAR(16) NULL;
ALTER TABLE videos ADD day INTEGER NULL;
ALTER TABLE videos ADD position VARCHAR(48) NULL;
ALTER TABLE videos ADD content_buffer TEXT NULL;
ALTER TABLE videos ADD secret_user_id TEXT NULL;
ALTER TABLE videos ADD player_count INTEGER NULL;

CREATE INDEX idx_videos_version ON videos (version);
CREATE INDEX idx_videos_day ON videos (day);
CREATE INDEX idx_videos_secret_user_id ON videos (secret_user_id);
CREATE INDEX idx_videos_player_count ON videos (player_count);
