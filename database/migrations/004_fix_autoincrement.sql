-- ============================================================
-- Migration: 004_fix_autoincrement
-- DuckDB에서 INTEGER PRIMARY KEY는 자동증가 아님.
-- SEQUENCE를 생성하고 DEFAULT nextval로 연결.
-- ============================================================

-- pdf_reports.id
CREATE SEQUENCE IF NOT EXISTS seq_pdf_reports_id START 1;
ALTER TABLE pdf_reports ALTER COLUMN id SET DEFAULT nextval('seq_pdf_reports_id');

-- data_update_log.id
CREATE SEQUENCE IF NOT EXISTS seq_data_update_log_id START 1;
ALTER TABLE data_update_log ALTER COLUMN id SET DEFAULT nextval('seq_data_update_log_id');

-- groups.group_id
CREATE SEQUENCE IF NOT EXISTS seq_groups_id START 1;
ALTER TABLE groups ALTER COLUMN group_id SET DEFAULT nextval('seq_groups_id');

-- watchlists.watchlist_id
CREATE SEQUENCE IF NOT EXISTS seq_watchlists_id START 1;
ALTER TABLE watchlists ALTER COLUMN watchlist_id SET DEFAULT nextval('seq_watchlists_id');
