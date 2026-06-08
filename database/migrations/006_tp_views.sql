-- 006_tp_views.sql
-- target_price_stocks / target_price_monthly 를 pdf_reports 기반 VIEW로 대체.
-- 기존 테이블이 남아 있으면 삭제 후 VIEW 재생성 (매 기동 시 재실행 안전).

DROP TABLE IF EXISTS target_price_stocks;
DROP TABLE IF EXISTS target_price_monthly;

-- ── target_price_stocks ───────────────────────────────────────
-- 종목별 최신 집계 (ticker 당 1행, date = 조회 당일)
CREATE OR REPLACE VIEW target_price_stocks AS
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

-- ── target_price_monthly ──────────────────────────────────────
-- 종목 × 월별 집계 (리포트 작성일 기준 월)
CREATE OR REPLACE VIEW target_price_monthly AS
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
