"""
============================================================
pdf_reports 테이블 뷰어
------------------------------------------------------------
python viewer.py
python viewer.py --status done --limit 20
python viewer.py --has-tp --limit 20
python viewer.py --status done --has-tp --csv result.csv
python viewer.py --db-path "C:/Users/tien7/AppData/Local/quant/quant.duckdb"


============================================================
"""

import argparse
import os
from pathlib import Path

import duckdb
import pandas as pd

def default_db_path() -> Path:
    return Path(
        os.environ.get(
            "QUANT_DB",
            Path.home() / "AppData" / "Local" / "quant" / "quant.duckdb",
        )
    )


def parse_args():
    p = argparse.ArgumentParser(description="pdf_reports viewer")
    p.add_argument("--db-path", default=None, help="DuckDB file path")
    p.add_argument("--table", default="pdf_reports", help="table name")
    p.add_argument("--status", default=None, help="analyze_status filter")
    p.add_argument("--has-tp", action="store_true", help="target_price IS NOT NULL")
    p.add_argument("--limit", type=int, default=50, help="row limit")
    p.add_argument("--tail", action="store_true", help="order by id ASC (oldest first)")
    p.add_argument("--csv", default=None, help="export result to csv path")
    return p.parse_args()


def build_query(table: str, status: str | None, has_tp: bool, limit: int, tail: bool) -> tuple[str, list]:
    where = []
    params: list = []

    if status:
        where.append("analyze_status = ?")
        params.append(status)
    if has_tp:
        where.append("target_price IS NOT NULL")

    where_sql = f"WHERE {' AND '.join(where)}" if where else ""
    order_sql = "ASC" if tail else "DESC"

    sql = f"""
    SELECT id, date, ticker, target_price,analyze_status
    FROM {table}
    {where_sql}
    ORDER BY id {order_sql}
    LIMIT ?
    """
    params.append(limit)
    return sql, params


def main():
    args = parse_args()
    db_path = Path(args.db_path).expanduser() if args.db_path else default_db_path()

    if not db_path.exists():
        raise FileNotFoundError(f"DB not found: {db_path}")

    con = duckdb.connect(str(db_path))
    try:
        sql, params = build_query(args.table, args.status, args.has_tp, args.limit, args.tail)
        df = con.execute(sql, params).fetchdf()
    finally:
        con.close()

    pd.set_option("display.max_colwidth", 180)
    pd.set_option("display.width", 200)
    pd.set_option("display.max_rows", min(max(args.limit, 1), 200))

    print(f"DB: {db_path}")
    print(f"Rows: {len(df)}")
    print(df)

    if args.csv:
        out = Path(args.csv).expanduser()
        df.to_csv(out, index=False, encoding="utf-8-sig")
        print(f"Saved CSV: {out}")


if __name__ == "__main__":
    main()
