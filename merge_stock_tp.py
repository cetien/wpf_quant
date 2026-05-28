
# //TODO: 

#     from source DB: tgt_stocks,tgt_monthly
#     -> merge into target DB: target_price_stocks, target_price_monthly
#     (현재 target DB에 해당 table은 data 없이 비어 있음)
#     (1회 수행 only. 주기적 실행 고려하지 않아도 됨)

#     SELECT DISTINCT LENGTH(fetch_date) FROM tgt_stocks; -> 8
#     SELECT DISTINCT LENGTH(ym) FROM tgt_monthly; -> 6

# source = C:\Users\tien7\source\repos\quant\moneyland.db (sqlite)
#     CREATE TABLE IF NOT EXISTS tgt_stocks (
#         fetch_date TEXT,    -- -> date
#         itm_cd     TEXT,     -- itm_cd.Trim() -> ticker
#         itm_nm     TEXT,    -- -> name
#         brk_cnt    INTEGER, -- -> report_count
#         avg_tgt    INTEGER,
#         min_tgt    INTEGER,
#         max_tgt    INTEGER,
#         cur_prc    INTEGER, -- -> cur_price
#         upside     REAL,
#         PRIMARY KEY (fetch_date, itm_cd)
#     );

#     data sample (tgt_stocks):
#         fetch_date|itm_cd|itm_nm              |brk_cnt|avg_tgt|min_tgt|max_tgt|cur_prc|upside|
#         ----------+------+--------------------+-------+-------+-------+-------+-------+------+
#         20260526  |000660|SK Hynix            |     26| 932813| 200000|3800000|1941000| -51.9|
#         20260526  |000270|기아                  |     26| 172117| 105000| 300000| 164800|   4.4|
#         20260526  |005930|삼성전자                |     26| 169793|  68000| 570000| 292500| -42.0|

#     CREATE TABLE IF NOT EXISTS tgt_monthly (
#         itm_cd     TEXT,    -- itm_cd.Trim() -> ticker
#         ym         TEXT,   -- 'YYYYMM'
#         brk_cnt    INTEGER,
#         avg_tgt    INTEGER,
#         min_tgt    INTEGER,
#         max_tgt    INTEGER,
#         price      INTEGER,
#         upside     REAL,
#         PRIMARY KEY (itm_cd, ym)
#     );

#     data sample (tgt_monthly):
#         itm_cd|ym    |brk_cnt|avg_tgt|min_tgt|max_tgt|price  |upside|
#         ------+------+-------+-------+-------+-------+-------+------+
#         000080|202412|      1|  27000|  27000|  27000|  19520|  38.3|
#         000080|202501|      6|  28333|  25000|  31000|  19060|  48.7|
#         000080|202503|      3|  28333|  25000|  32000|  19230|  47.3|

# target = C:\Users\tien7\AppData\Local\quant\quant.duckdb (duckdb)
#     CREATE TABLE IF NOT EXISTS target_price_stocks (
#         ticker     TEXT      NOT NULL,
#         date       DATE      NOT NULL,  -- YYYY-MM-DD
#         name       TEXT,
#         report_count    INTEGER,
#         avg_tgt    INTEGER,
#         min_tgt    INTEGER,
#         max_tgt    INTEGER,
#         cur_price    INTEGER,
#         upside     REAL,
#         PRIMARY KEY (ticker, date)
#     );
#     CREATE TABLE IF NOT EXISTS target_price_monthly (
#         ticker     TEXT    NOT NULL,
#         ym         TEXT    NOT NULL,   -- 'YYYYMM'
#         report_count    INTEGER,
#         avg_tgt    INTEGER,
#         min_tgt    INTEGER,
#         max_tgt    INTEGER,
#         price      INTEGER,
#         upside     REAL,
#         PRIMARY KEY (ticker, ym)
#     );

# migrate_target_price.py

import sqlite3
import duckdb
from datetime import datetime

SOURCE_DB = r"C:\Users\tien7\source\repos\quant\moneyland.db"
TARGET_DB = r"C:\Users\tien7\AppData\Local\quant\quant.duckdb"


def convert_date(yyyymmdd: str) -> str:
    """
    20260526 -> 2026-05-26
    """
    return datetime.strptime(yyyymmdd, "%Y%m%d").date().isoformat()


def migrate_stocks(src_conn, dst_conn):
    print("[INFO] Loading tgt_stocks...")

    rows = src_conn.execute("""
        SELECT
            fetch_date,
            itm_cd,
            itm_nm,
            brk_cnt,
            avg_tgt,
            min_tgt,
            max_tgt,
            cur_prc,
            upside
        FROM tgt_stocks
    """).fetchall()

    print(f"[INFO] Source rows : {len(rows):,}")

    data = [
        (
            r[1].strip(),          # ticker
            convert_date(r[0]),    # date
            r[2],                  # name
            r[3],                  # report_count
            r[4],
            r[5],
            r[6],
            r[7],                  # cur_price
            r[8],
        )
        for r in rows
    ]

    dst_conn.executemany("""
        INSERT INTO target_price_stocks (
            ticker,
            date,
            name,
            report_count,
            avg_tgt,
            min_tgt,
            max_tgt,
            cur_price,
            upside
        )
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, data)

    inserted = dst_conn.execute(
        "SELECT COUNT(*) FROM target_price_stocks"
    ).fetchone()[0]

    print(f"[INFO] Target rows : {inserted:,}")


def migrate_monthly(src_conn, dst_conn):
    print("[INFO] Loading tgt_monthly...")

    rows = src_conn.execute("""
        SELECT
            itm_cd,
            ym,
            brk_cnt,
            avg_tgt,
            min_tgt,
            max_tgt,
            price,
            upside
        FROM tgt_monthly
    """).fetchall()

    print(f"[INFO] Source rows : {len(rows):,}")

    data = [
        (
            r[0].strip(),  # ticker
            r[1],          # ym
            r[2],          # report_count
            r[3],
            r[4],
            r[5],
            r[6],
            r[7],
        )
        for r in rows
    ]

    dst_conn.executemany("""
        INSERT INTO target_price_monthly (
            ticker,
            ym,
            report_count,
            avg_tgt,
            min_tgt,
            max_tgt,
            price,
            upside
        )
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    """, data)

    inserted = dst_conn.execute(
        "SELECT COUNT(*) FROM target_price_monthly"
    ).fetchone()[0]

    print(f"[INFO] Target rows : {inserted:,}")


def main():
    print("[START] Target Price Migration")

    src_conn = sqlite3.connect(SOURCE_DB)
    dst_conn = duckdb.connect(TARGET_DB)

    try:
        stock_count = dst_conn.execute(
            "SELECT COUNT(*) FROM target_price_stocks"
        ).fetchone()[0]

        monthly_count = dst_conn.execute(
            "SELECT COUNT(*) FROM target_price_monthly"
        ).fetchone()[0]

        if stock_count > 0 or monthly_count > 0:
            raise RuntimeError(
                "Target tables are not empty. Migration aborted."
            )

        dst_conn.begin()

        migrate_stocks(src_conn, dst_conn)
        migrate_monthly(src_conn, dst_conn)

        dst_conn.commit()

        print("[SUCCESS] Migration completed.")

    except Exception:
        dst_conn.rollback()
        raise

    finally:
        src_conn.close()
        dst_conn.close()


if __name__ == "__main__":
    main()
    