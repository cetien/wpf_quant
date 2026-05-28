"""
============================================================
DuckDB 범용 쿼리 실행기 (Viewer)
------------------------------------------------------------

Usage:

--db-path "myfile.duckdb"
    - 미지정시 기본 경로: C:/Users/tien7/AppData/Local/quant/quant.duckdb

--limit 50
    - 출력할 최대 행 수 (기본값: 50)
    - not for SQL command, but for display limit : print(df.head(args.limit))

--csv "output.csv"
    - 쿼리 결과를 CSV로 저장
        
python viewer.py "SELECT * FROM pdf_reports LIMIT 20"
python viewer.py "SELECT analyze_status, count(*) FROM pdf_reports GROUP BY 1"
python viewer.py "SELECT * FROM daily_prices WHERE ticker='005930'" --csv samsung.csv
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
    p = argparse.ArgumentParser(description="DuckDB SQL runner")
    p.add_argument("query", help="SQL query command to execute")
    p.add_argument("--db-path", default=None, help="DuckDB file path")
    p.add_argument("--csv", default=None, help="export result to csv path")
    p.add_argument("--limit", type=int, default=50, help="limit rows for display (default: 50)")
    return p.parse_args()


def main():
    args = parse_args()
    db_path = Path(args.db_path).expanduser() if args.db_path else default_db_path()

    if not db_path.exists():
        raise FileNotFoundError(f"DB not found: {db_path}")

    con = duckdb.connect(str(db_path))
    try:
        df = con.execute(args.query).fetchdf()
    finally:
        con.close()

    pd.set_option("display.max_colwidth", 180)
    pd.set_option("display.width", 200)
    pd.set_option("display.max_rows", args.limit)

    print(f"DB: {db_path}")
    print(f"Rows: {len(df)}")
    print(df.head(args.limit))

    if args.csv:
        out = Path(args.csv).expanduser()
        df.to_csv(out, index=False, encoding="utf-8-sig")
        print(f"Saved CSV: {out}")


if __name__ == "__main__":
    main()
