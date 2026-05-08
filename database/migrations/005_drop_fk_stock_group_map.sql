-- ============================================================
-- Migration: 005_drop_fk_stock_group_map
-- 
-- 문제: DuckDB는 UPDATE 시 내부적으로 DELETE+INSERT로 재작성하며,
--       부모 테이블(groups) UPDATE 시 자식 테이블(stock_group_map)의
--       FK 참조 검사가 트리거되어 Constraint Error 발생.
--       DuckDB는 ALTER TABLE DROP CONSTRAINT 미지원.
--
-- 해결: stock_group_map을 FK 없이 재생성.
--       참조 정합성은 앱 레벨(Repository)에서 유지.
-- ============================================================

-- 1. 기존 데이터 백업
CREATE TABLE IF NOT EXISTS stock_group_map_backup AS
    SELECT * FROM stock_group_map;

-- 2. 기존 테이블 삭제 (FK 포함)
DROP TABLE IF EXISTS stock_group_map;

-- 3. FK 없이 재생성 (인덱스는 유지)
CREATE TABLE stock_group_map (
    ticker      TEXT        NOT NULL,
    group_id    INTEGER     NOT NULL,
    weight      DOUBLE      NOT NULL DEFAULT 1.0 CHECK (weight > 0),
    created_at  TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker, group_id)
);

CREATE INDEX IF NOT EXISTS idx_sgm_ticker ON stock_group_map(ticker);
CREATE INDEX IF NOT EXISTS idx_sgm_group  ON stock_group_map(group_id);

-- 4. 데이터 복원
INSERT INTO stock_group_map SELECT * FROM stock_group_map_backup;

-- 5. 백업 테이블 삭제
DROP TABLE stock_group_map_backup;
