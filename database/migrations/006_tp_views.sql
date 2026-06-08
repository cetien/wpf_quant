-- 006_tp_views.sql
-- target_price_stocks / target_price_monthly 를 VIEW로 대체.
-- 기존 테이블의 외부 출처 데이터는 _supplement 테이블로 보존 후 UNION.
--
-- 멱등성:
--   첫 실행  → TABLE → supplement 복사 → TABLE DROP → VIEW 생성
--   재실행   → VIEW  → supplement 그대로 → VIEW DROP → VIEW 재생성

-- ══════════════════════════════════════════════════════════════
--  target_price_monthly
-- ══════════════════════════════════════════════════════════════

-- 기존 테이블 데이터 보존 (IF NOT EXISTS → 재실행 시 덮어쓰기 방지)
CREATE TABLE IF NOT EXISTS target_price_supplement AS
    SELECT * FROM target_price_monthly;

-- TABLE이면 첫 번째, VIEW이면 두 번째가 처리
DROP TABLE IF EXISTS target_price_monthly;
DROP VIEW  IF EXISTS target_price_monthly;

-- PDF 리포트 월별 집계 (내부용)
CREATE OR REPLACE VIEW v_tp_monthly_pdf AS
SELECT
    r.ticker,
    strftime('%Y%m', r.date)                                              AS ym,
    COUNT(*)                                                              AS report_count,
    ROUND(AVG(r.target_price))::INTEGER                                  AS avg_tgt,
    MIN(r.target_price)::INTEGER                                         AS min_tgt,
    MAX(r.target_price)::INTEGER                                         AS max_tgt,
    c.current_price::INTEGER                                             AS price,
    ROUND((AVG(r.target_price) / NULLIF(c.current_price, 0) - 1) * 100, 2) AS upside
FROM pdf_reports r
LEFT JOIN stock_cache c ON c.ticker = r.ticker
WHERE r.target_price IS NOT NULL
  AND r.analyze_status NOT IN ('skip')
GROUP BY r.ticker, strftime('%Y%m', r.date), c.current_price;

-- 통합 뷰: PDF 우선, PDF 없는 (ticker, ym)은 supplement로 보완
CREATE OR REPLACE VIEW target_price_monthly AS
SELECT ticker, ym, report_count, avg_tgt, min_tgt, max_tgt, price, upside
FROM   v_tp_monthly_pdf
UNION ALL
SELECT ticker, ym, report_count, avg_tgt, min_tgt, max_tgt, price, upside
FROM   target_price_supplement
WHERE  (ticker, ym) NOT IN (SELECT ticker, ym FROM v_tp_monthly_pdf);


-- ══════════════════════════════════════════════════════════════
--  target_price_stocks
-- ══════════════════════════════════════════════════════════════

CREATE TABLE IF NOT EXISTS target_price_stocks_supplement AS
    SELECT * FROM target_price_stocks;

DROP TABLE IF EXISTS target_price_stocks;
DROP VIEW  IF EXISTS target_price_stocks;

-- PDF 리포트 종목별 현재 집계
CREATE OR REPLACE VIEW v_tp_stocks_pdf AS
SELECT
    r.ticker,
    CURRENT_DATE::DATE                                                    AS date,
    s.name,
    COUNT(*)                                                              AS report_count,
    ROUND(AVG(r.target_price))::INTEGER                                  AS avg_tgt,
    MIN(r.target_price)::INTEGER                                         AS min_tgt,
    MAX(r.target_price)::INTEGER                                         AS max_tgt,
    c.current_price::INTEGER                                             AS cur_price,
    ROUND((AVG(r.target_price) / NULLIF(c.current_price, 0) - 1) * 100, 2) AS upside
FROM pdf_reports r
LEFT JOIN v_stock_primary_group s ON s.ticker = r.ticker
LEFT JOIN stock_cache c           ON c.ticker = r.ticker
WHERE r.target_price IS NOT NULL
  AND r.analyze_status NOT IN ('skip')
GROUP BY r.ticker, s.name, c.current_price;

-- 통합 뷰: PDF 우선, PDF 없는 ticker는 supplement로 보완
CREATE OR REPLACE VIEW target_price_stocks AS
SELECT ticker, date, name, report_count, avg_tgt, min_tgt, max_tgt, cur_price, upside
FROM   v_tp_stocks_pdf
UNION ALL
SELECT ticker, date, name, report_count, avg_tgt, min_tgt, max_tgt, cur_price, upside
FROM   target_price_stocks_supplement
WHERE  ticker NOT IN (SELECT ticker FROM v_tp_stocks_pdf);
