-- ============================================================
-- Migration: 006_stock_cache_add_columns
-- stock_cache 테이블에 이동평균·거래량비율·고가 컬럼 추가
-- 기존 DB 업그레이드용. 신규 DB는 001_init_schema.sql에 이미 포함.
-- DuckDB: ADD COLUMN IF NOT EXISTS 미지원 → TRY/CATCH 없이 idempotent 하게 처리
-- ============================================================

ALTER TABLE stock_cache ADD COLUMN IF NOT EXISTS ma20         DOUBLE;  -- 20일 이동평균 (adj_close)
ALTER TABLE stock_cache ADD COLUMN IF NOT EXISTS ma60         DOUBLE;  -- 60일 이동평균
ALTER TABLE stock_cache ADD COLUMN IF NOT EXISTS ma120        DOUBLE;  -- 120일 이동평균
ALTER TABLE stock_cache ADD COLUMN IF NOT EXISTS volume_ratio DOUBLE;  -- 현재 거래량 / 20일 평균 거래량
ALTER TABLE stock_cache ADD COLUMN IF NOT EXISTS high_60d     DOUBLE;  -- 60거래일 고가
ALTER TABLE stock_cache ADD COLUMN IF NOT EXISTS high_120d    DOUBLE;  -- 120거래일 고가
