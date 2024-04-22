DROP TABLE IF EXISTS ratelimits;
CREATE TABLE IF NOT EXISTS ratelimits (
  ip VARCHAR(45) PRIMARY KEY,
  asn VARCHAR(45) NULL,
  timestamp TIMESTAMP,
  type VARCHAR(32),
  count INTEGER
);

CREATE UNIQUE INDEX idx_ratelimits_ip ON ratelimits (ip);
CREATE INDEX idx_ratelimits_timestamp ON ratelimits (timestamp);
CREATE INDEX idx_ratelimits_asn ON ratelimits (asn);
CREATE INDEX idx_ratelimits_type ON ratelimits (type);
CREATE INDEX idx_ratelimits_count ON ratelimits (count);
