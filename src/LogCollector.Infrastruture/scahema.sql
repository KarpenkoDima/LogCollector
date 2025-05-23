CREATE TABLE IF NOT EXISTS logs(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source TEXT NOT NULL,
    timestamp TEXT NOT NULL, -- ISO 8601
    level TEXT NOT NULL,
    message TEXT NOT NULL,
    source_ip TEXT,
    hostmase TEXT
);

CREATE INDEX IF NOT EXISTS idx_logs_timestamp ON logs(timestamp);
CREATE INDEX IF NOT EXISTS idx_logs_source ON logs(source);