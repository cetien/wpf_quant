-- ============================================================
-- Migration: 003_options
-- 앱 설정을 key-value 형태로 저장
-- ============================================================

CREATE TABLE IF NOT EXISTS options (
    key        TEXT      NOT NULL PRIMARY KEY,
    value      TEXT,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- 기본값 seed
INSERT INTO options (key, value) VALUES ('auto_append_history', 'true') ON CONFLICT (key) DO NOTHING;
INSERT INTO options (key, value) VALUES ('report_pdf_folder',   '')     ON CONFLICT (key) DO NOTHING;
