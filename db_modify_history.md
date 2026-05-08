
CREATE TABLE stock_group_map_backup AS
SELECT * FROM stock_group_map;

CREATE TABLE daily_prices_backup AS
SELECT * FROM daily_prices;

DROP TABLE stock_group_map;
DROP TABLE daily_prices;

CREATE TABLE groups_new (
    group_id    INTEGER     PRIMARY KEY,
    kind        TEXT        NOT NULL,
    name        TEXT        NOT NULL,
    description TEXT,
    rating      INTEGER     NOT NULL DEFAULT 5,
    is_active   BOOLEAN     NOT NULL DEFAULT TRUE,
    created_at  TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP
);


INSERT INTO groups_new
SELECT *
FROM groups;

CREATE UNIQUE INDEX ux_groups_name
ON groups_new(name);

CREATE TABLE stocks_new (
    ticker          TEXT        NOT NULL,
    name            TEXT        NOT NULL,
    market          TEXT        NOT NULL,
    security_type   TEXT        NOT NULL,
    listed_date     DATE,
    rating          INTEGER     NOT NULL DEFAULT 5,
    is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
    updated_at      TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker)
);

INSERT INTO stocks_new
SELECT *
FROM stocks;

DROP TABLE groups;
DROP TABLE stocks;

----------------------------------------------------
DROP INDEX ux_groups_name;

ALTER TABLE groups_new RENAME TO groups;
ALTER TABLE stocks_new RENAME TO stocks;

CREATE UNIQUE INDEX ux_groups_name
ON groups(name);


CREATE TABLE stock_group_map (
    ticker      TEXT        NOT NULL REFERENCES stocks(ticker),
    group_id    INTEGER     NOT NULL REFERENCES groups(group_id),
    weight      DOUBLE      NOT NULL DEFAULT 1.0 CHECK (weight > 0),
    created_at  TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (ticker, group_id)
);

CREATE TABLE daily_prices (
    ticker      TEXT    NOT NULL REFERENCES stocks(ticker),
    date        DATE    NOT NULL,
    open        DOUBLE  NOT NULL CHECK (open > 0),
    high        DOUBLE  NOT NULL CHECK (high >= low),
    low         DOUBLE  NOT NULL CHECK (low > 0),
    close       DOUBLE  NOT NULL CHECK (close > 0),
    adj_close   DOUBLE  NOT NULL CHECK (adj_close > 0),
    volume      BIGINT  NOT NULL CHECK (volume >= 0),
    amount      BIGINT           CHECK (amount >= 0),
    PRIMARY KEY (ticker, date)
);

INSERT INTO stock_group_map
SELECT * FROM stock_group_map_backup;

INSERT INTO daily_prices
SELECT * FROM daily_prices_backup;

CREATE INDEX idx_sgm_ticker ON stock_group_map(ticker);
CREATE INDEX idx_sgm_group  ON stock_group_map(group_id);

CREATE SEQUENCE IF NOT EXISTS seq_groups_id START 1;

ALTER TABLE groups
ALTER COLUMN group_id
SET DEFAULT nextval('seq_groups_id');

SELECT setval(
    'seq_groups_id',
    (SELECT COALESCE(MAX(group_id), 0) + 1 FROM groups)
);

DROP TABLE stock_group_map_backup;
DROP TABLE daily_prices_backup;



