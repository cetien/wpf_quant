
-- ============================================================
-- Views  (DuckDB: CREATE OR REPLACE VIEW)
-- ============================================================

-- ── v_sectors ────────────────────────────────────────────────
CREATE OR REPLACE VIEW v_sectors AS
SELECT group_id AS sector_id, name, description, rating, is_active
FROM groups WHERE kind = 'sector';

-- ── v_themes ─────────────────────────────────────────────────
CREATE OR REPLACE VIEW v_themes AS
SELECT group_id AS theme_id, name, description, rating, is_active
FROM groups WHERE kind = 'theme';

-- ── v_active_group_map ───────────────────────────────────────
CREATE OR REPLACE VIEW v_active_group_map AS
SELECT
    m.ticker,
    s.name      AS stock_name,
    s.market,
    g.group_id,
    g.kind,
    g.name      AS group_name,
    g.rating    AS group_rating,
    m.weight
FROM stock_group_map m
JOIN stocks s ON s.ticker   = m.ticker
JOIN groups g ON g.group_id = m.group_id
WHERE g.is_active = TRUE AND s.is_active = TRUE;

-- 참고: v_active_theme_map = v_active_group_map WHERE kind = 'theme'

-- ── v_stock_primary_sector ───────────────────────────────────
CREATE OR REPLACE VIEW v_stock_primary_sector AS
SELECT
    s.ticker,
    s.name,
    s.market,
    s.rating,
    g.name      AS sector,
    g.group_id  AS sector_id,
    m.weight    AS sector_weight
FROM stocks s
JOIN (
    SELECT m.ticker, m.group_id, m.weight,
           ROW_NUMBER() OVER (PARTITION BY m.ticker ORDER BY m.weight DESC) AS rn
    FROM stock_group_map m
    JOIN groups g ON g.group_id = m.group_id
    WHERE g.kind = 'sector'
) m ON m.ticker = s.ticker AND m.rn = 1
JOIN groups g ON g.group_id = m.group_id;

-- ── v_sector_performance ─────────────────────────────────────
-- 섹터별 평균 수익률 (히트맵 표시용)
-- CREATE OR REPLACE VIEW v_sector_performance AS
-- SELECT
--     g.name          AS sector,
--     g.group_id      AS sector_id,
--     AVG(sc.ret_1m)  AS avg_ret_1m,
--     AVG(sc.ret_3m)  AS avg_ret_3m,
--     AVG(sc.ret_6m)  AS avg_ret_6m,
--     AVG(sc.rs)      AS avg_rs,
--     COUNT(sc.ticker) AS stock_count
-- FROM stock_cache sc
-- JOIN stock_group_map m  ON m.ticker   = sc.ticker
-- JOIN groups g           ON g.group_id = m.group_id
-- WHERE g.kind = 'sector' AND g.is_active = TRUE
-- GROUP BY g.name, g.group_id;
