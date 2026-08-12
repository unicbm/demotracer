CREATE TABLE IF NOT EXISTS event_receipts (
    event_id TEXT PRIMARY KEY,
    expires_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS event_receipts_expires_at
    ON event_receipts (expires_at);

CREATE TABLE IF NOT EXISTS active_installations (
    daily_id TEXT PRIMARY KEY,
    app_version TEXT NOT NULL,
    playback_version TEXT NOT NULL,
    last_seen INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS active_installations_last_seen
    ON active_installations (last_seen);

CREATE TABLE IF NOT EXISTS daily_installations (
    day TEXT NOT NULL,
    daily_id TEXT NOT NULL,
    app_version TEXT NOT NULL,
    playback_version TEXT NOT NULL,
    first_seen INTEGER NOT NULL,
    PRIMARY KEY (day, daily_id)
);

CREATE INDEX IF NOT EXISTS daily_installations_day
    ON daily_installations (day);

CREATE TABLE IF NOT EXISTS hourly_metrics (
    hour TEXT NOT NULL,
    event_kind TEXT NOT NULL,
    outcome TEXT NOT NULL,
    app_version TEXT NOT NULL,
    playback_version TEXT NOT NULL,
    demo_source TEXT NOT NULL,
    error_code TEXT NOT NULL,
    rounds_bucket TEXT NOT NULL,
    duration_bucket TEXT NOT NULL,
    event_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (
        hour,
        event_kind,
        outcome,
        app_version,
        playback_version,
        demo_source,
        error_code,
        rounds_bucket,
        duration_bucket
    )
);

CREATE INDEX IF NOT EXISTS hourly_metrics_hour
    ON hourly_metrics (hour);

CREATE TABLE IF NOT EXISTS daily_rollups (
    day TEXT PRIMARY KEY,
    active_installations INTEGER NOT NULL,
    analysis_success INTEGER NOT NULL,
    analysis_failure INTEGER NOT NULL,
    conversion_success INTEGER NOT NULL,
    conversion_failure INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);
