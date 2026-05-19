-- ============================================================
-- Quant DB Schema v1.0  (DuckDB)
-- Migration: 001_init_schema
-- ============================================================

-- ── stocks ──────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS stocks (
    ticker          TEXT        NOT NULL,
    name            TEXT        NOT NULL,
    market          TEXT        NOT NULL,-- CHECK (market IN ('KP', 'KQ', 'NYSE')),
    security_type   TEXT        NOT NULL,   -- 'stock' | 'index' | 'ETF'
    listed_date     DATE,
    rating          INTEGER     NOT NULL DEFAULT 5,
    is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
    updated_at      TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker)
);

-- ── delisted_stocks ─────────────────────────────────────────
-- 상폐 이력: 기존 DB 이전 시 필요, 이후 is_active=FALSE 운용으로 대체 검토
-- CREATE TABLE IF NOT EXISTS delisted_stocks (
--     ticker          TEXT        NOT NULL,
--     name            TEXT,
--     delist_date     DATE        NOT NULL,
--     delist_reason   TEXT,
--     PRIMARY KEY (ticker)
-- );

-- ── groups (sector + theme 통합) ────────────────────────────
CREATE TABLE IF NOT EXISTS groups (
    group_id    INTEGER     PRIMARY KEY,
    kind        TEXT        NOT NULL,-- CHECK (kind IN ('sector', 'theme', 'watch')),
    name        TEXT        NOT NULL,-- UNIQUE,
    description TEXT,
    rating      INTEGER     NOT NULL DEFAULT 5,
    is_active   BOOLEAN     NOT NULL DEFAULT TRUE,
    created_at  TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ── stock_group_map ─────────────────────────────────────────
CREATE TABLE IF NOT EXISTS stock_group_map (
    ticker      TEXT        NOT NULL,-- REFERENCES stocks(ticker),
    group_id    INTEGER     NOT NULL,-- REFERENCES groups(group_id),
    weight      DOUBLE      NOT NULL DEFAULT 1.0 CHECK (weight > 0),
    created_at  TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker, group_id)
);


CREATE INDEX IF NOT EXISTS idx_sgm_ticker ON stock_group_map(ticker);
CREATE INDEX IF NOT EXISTS idx_sgm_group  ON stock_group_map(group_id);

-- ── fundamentals ────────────────────────────────────────────
-- Look-ahead bias 방지: announce_date 기준 join
-- announce_date 소스: DART API (미확정 → 수동 입력 fallback)
CREATE TABLE IF NOT EXISTS fundamentals (
    ticker              TEXT    NOT NULL,-- REFERENCES stocks(ticker),
    report_date         DATE    NOT NULL,   -- 결산 기준일
    announce_date       DATE,               -- 공시 실제 날짜 (join 기준)
    fiscal_quarter      TEXT,               -- 예: '2024Q4'
    eps                 DOUBLE,
    per                 DOUBLE,             -- 음수 가능(적자), CHECK 제거
    pbr                 DOUBLE,--  CHECK (pbr > 0),
    roe                 DOUBLE,
    revenue             BIGINT,
    operating_income    BIGINT,
    net_income          BIGINT,             -- ROE 계산 시 필요
    debt_ratio          DOUBLE,
    ingested_at         TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker, report_date, fiscal_quarter)
);

-- ── daily_prices ─────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS daily_prices (
    ticker      TEXT    NOT NULL REFERENCES stocks(ticker),
    date        DATE    NOT NULL,
    open        DOUBLE  NOT NULL CHECK (open > 0),
    high        DOUBLE  NOT NULL CHECK (high >= low),
    low         DOUBLE  NOT NULL CHECK (low > 0),
    close       DOUBLE  NOT NULL CHECK (close > 0),
    adj_close   DOUBLE  NOT NULL CHECK (adj_close > 0),
    volume      BIGINT  NOT NULL CHECK (volume >= 0),
    amount      BIGINT           CHECK (amount >= 0),  -- pykrx 제공, yfinance 미제공
    PRIMARY KEY (ticker, date)
);

-- ── supply ───────────────────────────────────────────────────
-- Phase 1 MVP: 수급 포함
-- pykrx: 주 수량 기준 / KIS API: 금액 기준 → Python 워커 유지로 pykrx 사용
CREATE TABLE IF NOT EXISTS supply (
    ticker              TEXT    NOT NULL,-- REFERENCES stocks(ticker),
    date                DATE    NOT NULL,
    inst_net_buy        BIGINT,             -- 기관 순매수 (주)
    foreign_net_buy     BIGINT,             -- 외인 순매수 (주)
    inst_net_amount     BIGINT,             -- 기관 순매수 (금액, 원)
    foreign_net_amount  BIGINT,             -- 외인 순매수 (금액, 원)
    ingested_at         TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker, date)
);

-- ── stock_cache ──────────────────────────────────────────────
-- 현재는 대시보드 고속 표시용 유지
CREATE TABLE IF NOT EXISTS stock_cache (
     ticker      TEXT    NOT NULL, -- REFERENCES stocks(ticker),
     asof_date   DATE    NOT NULL,
     current_price DOUBLE,                  -- 현재가
     ret_1m      DOUBLE,                     -- 30일 수익률 (adj_close 기준)
     ret_3m      DOUBLE,                     -- 90일 수익률
     ret_6m      DOUBLE,                     -- 180일 수익률
     ret_1y      DOUBLE,                     -- 1년 수익률
     rs          DOUBLE,                     -- KOSPI 대비 상대강도
     -- beta_60d    DOUBLE,                     -- 60일 베타 (Phase 1 UI 요건)
     eps         DOUBLE,
     per         DOUBLE,
     pbr         DOUBLE,
     roe         DOUBLE,
    -- market_cap  DOUBLE, 
    atr_14      DOUBLE, -- Average True Range, 14일 평균 변동폭
    atr_percent DOUBLE,
    high_52w    DOUBLE, 
    low_52w     DOUBLE, 
    volume_avg_20d  DOUBLE, 
    distance_from_high  DOUBLE, 
     updated_at  TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
     PRIMARY KEY (ticker)
);
-- rebuild: staleness check
--      SELECT MAX(date) FROM daily_prices
--      SELECT MAX(asof_date) FROM stock_cache
--      if cache_date < daily_price_date: full_rebuild (CREATE OR REPLACE TABLE stock_cache AS ...)
-- 참고: current_data <=== SELECT MAX(date) FROM daily_prices WHERE ticker=?


-- ── watchlists ───────────────────────────────────────────────
-- CREATE TABLE IF NOT EXISTS watchlists (
--     watchlist_id    INTEGER     PRIMARY KEY,
--     name            TEXT        NOT NULL,   -- "주도주", "ETF", "방산", "저평가"
--     description     TEXT,
--     created_at      TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
--     updated_at      TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
--     is_active       BOOLEAN     NOT NULL DEFAULT TRUE
-- );
-- 
-- 최근조회 watchlist: UI에서 자동등록 (watchlist_id = 1 예약)

-- ── watchlist_items ──────────────────────────────────────────
-- CREATE TABLE IF NOT EXISTS watchlist_items (
--     watchlist_id    INTEGER     NOT NULL REFERENCES watchlists(watchlist_id),
--     ticker          TEXT        NOT NULL REFERENCES stocks(ticker),
--     added_at        TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
--     rating          INTEGER     NOT NULL DEFAULT 5,
--     is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
--     trigger_reason  TEXT,       -- 수급 돌파, 저평가, 리포트, 이벤트 등
--     PRIMARY KEY (watchlist_id, ticker)
);

-- ── pdf_reports ──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS pdf_reports (
    id          INTEGER     PRIMARY KEY,
    date        DATE        NOT NULL,
    ticker      TEXT,
    title       TEXT,
    writer      TEXT,
    filepath    TEXT        UNIQUE,
    file_hash   TEXT        UNIQUE,
    created_at  TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ── data_update_log ──────────────────────────────────────────
-- 수집 실패 추적 / 데이터 결손 탐지
CREATE TABLE IF NOT EXISTS data_update_log (
    id          INTEGER     PRIMARY KEY,
    ticker      TEXT,
    date        DATE,
    source      TEXT        NOT NULL,   -- 'pykrx', 'dart', 'manual'
    status      TEXT        NOT NULL,   -- 'success', 'fail', 'skip'
    error_msg   TEXT,
    run_at      TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ── trading_calendar ─────────────────────────────────────────
-- 영업일 계산 (N일 수익률, RS 산출 필수)
-- CREATE TABLE IF NOT EXISTS trading_calendar (
--     date            DATE        NOT NULL,
--     is_trading_day  BOOLEAN     NOT NULL DEFAULT TRUE,
--     PRIMARY KEY (date)
-- );
