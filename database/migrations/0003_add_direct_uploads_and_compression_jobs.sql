ALTER TABLE christmas_list_items
    ADD COLUMN image_status TEXT NOT NULL DEFAULT 'ready';

ALTER TABLE christmas_list_items
    ADD CONSTRAINT christmas_list_items_image_status_check
    CHECK (image_status IN ('uploading', 'processing', 'ready', 'failed'));

CREATE TABLE compression_jobs (
    item_id UUID PRIMARY KEY REFERENCES christmas_list_items(id) ON DELETE CASCADE,
    source_object_id TEXT NOT NULL,
    expected_content_type TEXT NOT NULL,
    expected_size BIGINT NOT NULL CHECK (expected_size > 0),
    compressed_object_id TEXT,
    status TEXT NOT NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    available_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    lease_expires_at TIMESTAMPTZ,
    upload_expires_at TIMESTAMPTZ,
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TIMESTAMPTZ,
    CONSTRAINT compression_jobs_status_check
        CHECK (status IN ('awaiting_upload', 'queued', 'processing', 'cleanup_pending', 'abandoned_upload', 'completed', 'failed'))
);

CREATE INDEX compression_jobs_ready_idx
    ON compression_jobs (available_at)
    WHERE status IN ('queued', 'processing', 'cleanup_pending', 'abandoned_upload');

CREATE INDEX compression_jobs_upload_expiry_idx
    ON compression_jobs (upload_expires_at)
    WHERE status = 'awaiting_upload';
