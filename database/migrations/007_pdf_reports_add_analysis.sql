-- ============================================================
-- Migration: 007_pdf_reports_add_analysis
-- pdf_reports 테이블에 Phase 1 분석 컬럼 추가
-- ============================================================

ALTER TABLE pdf_reports ADD COLUMN IF NOT EXISTS target_price    DOUBLE;
ALTER TABLE pdf_reports ADD COLUMN IF NOT EXISTS analyze_status  TEXT DEFAULT 'pending';
-- 'pending' | 'done' | 'failed' | 'skip'
-- pending : 미분석 (신규 ingest 기본값)
-- done    : 정규식 추출 성공 (target_price NULL 포함 — 표현 없음도 done)
-- failed  : pdfplumber 예외 발생
-- skip    : 1페이지 텍스트 추출 결과 빈 문자열 (스캔본 등)
