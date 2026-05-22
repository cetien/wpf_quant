-- ============================================================
-- Migration: 008_stock_cache_add_report_info
-- stock_cache 테이블에 리포트 관련 컬럼 추가
-- ============================================================

ALTER TABLE stock_cache ADD COLUMN IF NOT EXISTS report_count INTEGER;
ALTER TABLE stock_cache ADD COLUMN IF NOT EXISTS target_price DOUBLE;
