-- ── stock_tp ─────────────────────────────────────────────────
-- PDF 없이 웹/수동으로 파악한 목표주가 저장.
-- stock_cache.target_price = pdf_reports + stock_tp 중 최신값 (UNION 방식).
-- source: 'manual' | 'web' | 'news'
CREATE TABLE IF NOT EXISTS stock_tp (
    ticker        TEXT      NOT NULL,
    date          DATE      NOT NULL,
    target_price  DOUBLE    NOT NULL CHECK (target_price > 0),
    source        TEXT      NOT NULL DEFAULT 'manual',
    note          TEXT,                                          -- 출처 URL (나중에 UI 연동)
    created_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker, date, source)
);
