"""
storage/schema.py
DuckDB 테이블 DDL 정의.
- STRICT 모드 대신 컬럼 타입 명시 + CHECK 제약으로 데이터 정밀도 보장
- announce_date 컬럼으로 Look-ahead bias 원천 차단
- delisted_stocks 테이블로 생존 편향 방지
- sectors / stock_sector_map / themes / stock_theme_map 추가
"""

# ── 섹터 마스터 (stocks보다 먼저 생성: FK 참조 대상) ─────────────────────────

CREATE_SECTORS = """
CREATE TABLE IF NOT EXISTS sectors (
    id      INTEGER PRIMARY KEY,
    name    TEXT    NOT NULL UNIQUE,
    rating  INTEGER NOT NULL DEFAULT 5   -- 검색 우선순위 가중치
);
"""

# ── 기준 테이블 ──────────────────────────────────────────────────────────────

CREATE_STOCKS = """
CREATE TABLE IF NOT EXISTS stocks (
    ticker          TEXT        NOT NULL,
    name            TEXT        NOT NULL,
    market          TEXT        NOT NULL CHECK (market IN ('KOSPI', 'KOSDAQ')),
    industry        TEXT,
    listed_date     DATE,
    rating          INTEGER     NOT NULL DEFAULT 5,
    is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
    updated_at      TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker)
);
"""

CREATE_DELISTED = """
CREATE TABLE IF NOT EXISTS delisted_stocks (
    ticker          TEXT        NOT NULL,
    name            TEXT,
    delist_date     DATE        NOT NULL,
    delist_reason   TEXT,
    PRIMARY KEY (ticker)
);
"""

# ── 종목-섹터 매핑 (N:M, 복수 섹터 허용) ─────────────────────────────────────

CREATE_STOCK_SECTOR_MAP = """
CREATE TABLE IF NOT EXISTS stock_sector_map (
    ticker      TEXT    NOT NULL REFERENCES stocks(ticker),
    sector_id   INTEGER NOT NULL REFERENCES sectors(id),
    weight      DOUBLE  NOT NULL DEFAULT 1.0
                        CHECK (weight > 0),  -- 복수 섹터 시 비중 (검색 우선순위용)
    PRIMARY KEY (ticker, sector_id)
);
"""

# ── 가격 테이블 ──────────────────────────────────────────────────────────────

CREATE_DAILY_PRICES = """
CREATE TABLE IF NOT EXISTS daily_prices (
    ticker          TEXT        NOT NULL REFERENCES stocks(ticker),
    date            DATE        NOT NULL,
    open            DOUBLE      NOT NULL CHECK (open > 0),
    high            DOUBLE      NOT NULL CHECK (high >= open),
    low             DOUBLE      NOT NULL CHECK (low <= open),
    close           DOUBLE      NOT NULL CHECK (close > 0),
    adj_close       DOUBLE      NOT NULL CHECK (adj_close > 0),
    volume          BIGINT      NOT NULL CHECK (volume >= 0),
    amount          BIGINT               CHECK (amount >= 0),  -- pykrx 제공, yfinance 미제공
    ingested_at     TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker, date)
);
"""


# ── 수급 테이블 ──────────────────────────────────────────────────────────────

CREATE_SUPPLY = """
CREATE TABLE IF NOT EXISTS supply (
    ticker              TEXT    NOT NULL REFERENCES stocks(ticker),
    date                DATE    NOT NULL,
    inst_net_buy        BIGINT,         -- 기관 순매수 (주)
    foreign_net_buy     BIGINT,         -- 외인 순매수 (주)
    inst_net_amount     BIGINT,         -- 기관 순매수 (금액)
    foreign_net_amount  BIGINT,         -- 외인 순매수 (금액)
    ingested_at         TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker, date)
);
"""

# ── 매크로 지표 테이블 ───────────────────────────────────────────────────────

CREATE_MACRO = """
CREATE TABLE IF NOT EXISTS macro_indicators (
    indicator_code  TEXT        NOT NULL,   -- 예: 'USD_KRW', 'SOX', 'WTI'
    date            DATE        NOT NULL,
    value           DOUBLE      NOT NULL,
    change_rate     DOUBLE,                 -- 전일 대비 등락률
    ingested_at     TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (indicator_code, date)
);
"""

# ── 재무 테이블 (Look-ahead bias 방지: announce_date 기준 join) ──────────────

CREATE_FUNDAMENTALS = """
CREATE TABLE IF NOT EXISTS fundamentals (
    ticker          TEXT        NOT NULL REFERENCES stocks(ticker),
    report_date     DATE        NOT NULL,   -- 결산 기준일
    announce_date   DATE,                   -- 공시 실제 날짜 (join 기준)
    fiscal_quarter  TEXT,                   -- 예: '2024Q4'
    eps             DOUBLE,
    per             DOUBLE      CHECK (per > 0),
    pbr             DOUBLE      CHECK (pbr > 0),
    roe             DOUBLE,
    revenue         BIGINT,
    operating_income BIGINT,
    debt_ratio      DOUBLE,
    ingested_at     TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker, report_date, fiscal_quarter)
);
"""

# ── 거래일 캘린더 테이블 ─────────────────────────────────────────────────────

CREATE_TRADING_CALENDAR = """
CREATE TABLE IF NOT EXISTS trading_calendar (
    market          TEXT    NOT NULL,   -- 'KRX', 'NYSE' 등
    date            DATE    NOT NULL,
    is_open         BOOLEAN NOT NULL DEFAULT TRUE,
    PRIMARY KEY (market, date)
);
"""


# ── 테마 마스터 ──────────────────────────────────────────────────────────────

CREATE_THEMES = """
CREATE TABLE IF NOT EXISTS themes (
    theme_id    INTEGER PRIMARY KEY,
    name        TEXT    NOT NULL UNIQUE,
    description TEXT,
    rating      INTEGER NOT NULL DEFAULT 5,
    is_active   BOOLEAN NOT NULL DEFAULT TRUE,
    created_at  TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);
"""

# ── 종목-테마 매핑 (N:M, 이력 관리) ──────────────────────────────────────────

CREATE_STOCK_THEME_MAP = """
CREATE TABLE IF NOT EXISTS stock_theme_map (
    ticker      TEXT    NOT NULL REFERENCES stocks(ticker),
    theme_id    INTEGER NOT NULL REFERENCES themes(theme_id),
    weight      DOUBLE  NOT NULL DEFAULT 1.0
                        CHECK (weight > 0),     -- 테마 내 종목 중요도
    confidence  DOUBLE           DEFAULT NULL
                        CHECK (confidence >= 0 AND confidence <= 1),
    source      TEXT    NOT NULL DEFAULT 'manual'
                        CHECK (source IN ('manual', 'rule', 'nlp')),
    valid_from  DATE    NOT NULL DEFAULT CURRENT_DATE,
    valid_to    DATE             DEFAULT NULL,  -- NULL = 현재 유효
    created_at  TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker, theme_id, valid_from)  -- valid_from 포함: 재활성화 이력 허용
);
"""

CREATE_IDX_STM_TICKER = """
CREATE INDEX IF NOT EXISTS idx_stm_ticker ON stock_theme_map(ticker);
"""

CREATE_IDX_STM_THEME = """
CREATE INDEX IF NOT EXISTS idx_stm_theme ON stock_theme_map(theme_id);
"""

# ── 뷰: 현재 유효 테마 종목만 ────────────────────────────────────────────────

CREATE_VIEW_ACTIVE_THEME = """
CREATE OR REPLACE VIEW v_active_theme_map AS
SELECT
    stm.ticker,
    s.name,
    s.market,
    t.theme_id,
    t.name        AS theme_name,
    stm.weight,
    stm.confidence,
    stm.source,
    stm.valid_from
FROM stock_theme_map stm
JOIN stocks s  ON s.ticker   = stm.ticker
JOIN themes t  ON t.theme_id = stm.theme_id
WHERE (stm.valid_to IS NULL OR stm.valid_to > CURRENT_DATE)
  AND t.is_active = TRUE;
"""

# ── 뷰: 종목 + 대표 섹터 (weight 최대 섹터 기준) ─────────────────────────────

CREATE_VIEW_STOCK_PRIMARY_SECTOR = """
CREATE OR REPLACE VIEW v_stock_primary_sector AS
SELECT
    s.ticker,
    s.name,
    s.market,
    sec.name  AS sector,
    sec.id    AS sector_id,
    ssm.weight AS sector_weight
FROM stocks s
JOIN (
    SELECT ticker, sector_id, weight,
           ROW_NUMBER() OVER (PARTITION BY ticker ORDER BY weight DESC) AS rn
    FROM stock_sector_map
) ssm ON ssm.ticker = s.ticker AND ssm.rn = 1
JOIN sectors sec ON sec.id = ssm.sector_id;
"""


# ── 종목 캐시 (상승률 등 사전계산, side panel 고속 표시용) ──────────────────────

CREATE_STOCK_CACHE = """
CREATE TABLE IF NOT EXISTS stock_cache (
    ticker      TEXT        NOT NULL REFERENCES stocks(ticker),
    last_date   DATE        NOT NULL,       -- incremental_update 기준일
    ret_1m      DOUBLE,                     -- 30일 수익률 (adj_close 기준)
    ret_3m      DOUBLE,                     -- 90일 수익률
    ret_6m      DOUBLE,                     -- 180일 수익률
    per         DOUBLE,                     -- 최신 PER (fundamentals join)
    pbr         DOUBLE,                     -- 최신 PBR
    roe         DOUBLE,                     -- 최신 ROE
    updated_at  TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker)
);
"""

# ── 조회 히스토리 ────────────────────────────────────────────────────────────

CREATE_STOCK_HISTORY = """
CREATE TABLE IF NOT EXISTS stock_history (
    ticker      TEXT      NOT NULL,          -- stocks FK (매크로는 indicator_code)
    kind        TEXT      NOT NULL DEFAULT 'stock'
                          CHECK (kind IN ('stock', 'macro')),
    name        TEXT      NOT NULL,          -- 표시명 캐시 (stocks.name / indicator_code)
    viewed_at   TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_pinned   BOOLEAN   NOT NULL DEFAULT FALSE,
    PRIMARY KEY (ticker, kind)               -- 종목+매크로 각각 1행
);
"""

# ── PDF 리포트 테이블 ────────────────────────────────────────────────────────

CREATE_PDF_REPORTS = """
CREATE TABLE IF NOT EXISTS pdf_reports (
    id          INTEGER PRIMARY KEY,
    date        DATE NOT NULL,
    ticker      TEXT,
    title       TEXT,
    writer      TEXT,
    filepath    TEXT UNIQUE,
    file_hash   TEXT UNIQUE,
    created_at  TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
"""

# ── 인제션 Checkpoint ────────────────────────────────────────────────────────

CREATE_INGEST_CHECKPOINT = """
CREATE TABLE IF NOT EXISTS ingest_checkpoint (
    source_key  TEXT PRIMARY KEY,
    last_date   TEXT NOT NULL,       -- YYYYMMDD (파일명 prefix 비교용, 문자열 정렬)
    updated_at  TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    file_count  INTEGER DEFAULT 0    -- 해당 source_key 누적 처리 건수
);
"""

# ── 전체 DDL 실행 순서 (FK 의존성 순서 준수) ─────────────────────────────────

ALL_SCHEMAS = [
    # 마스터 (의존 없음)
    CREATE_SECTORS,
    CREATE_STOCKS,
    CREATE_DELISTED,
    CREATE_THEMES,
    # 매핑 (stocks, sectors, themes 이후)
    CREATE_STOCK_SECTOR_MAP,
    CREATE_STOCK_THEME_MAP,
    # 인덱스
    CREATE_IDX_STM_TICKER,
    CREATE_IDX_STM_THEME,
    # 시계열 (stocks 이후)
    CREATE_DAILY_PRICES,
    CREATE_SUPPLY,
    CREATE_MACRO,
    CREATE_FUNDAMENTALS,
    CREATE_TRADING_CALENDAR,
    # 캐시 (stocks + daily_prices 이후)
    CREATE_STOCK_CACHE,
    # 히스토리
    CREATE_STOCK_HISTORY,
    # 뷰 (모든 테이블 이후)
    CREATE_VIEW_ACTIVE_THEME,
    CREATE_VIEW_STOCK_PRIMARY_SECTOR,
    # 리포트
    CREATE_PDF_REPORTS,
    # 인제션 checkpoint (의존 없음)
    CREATE_INGEST_CHECKPOINT,
]
