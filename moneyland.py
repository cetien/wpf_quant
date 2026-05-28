import requests
import re
import json
import sqlite3
from datetime import datetime, timedelta



# 정기 수집 방법 (일별 스냅샷 누적)
# 파일명이 tgtprc_YYYYMMDD.html 패턴이므로 날짜만 바꿔서 매일 cron 실행하면 됩니다.
# bash# crontab - 매 영업일 오후 7시 수집
# 0 19 * * 1-5 python /path/to/collect.py
# 과거 데이터도 날짜를 순회하면 backfill 가능합니다. MONTHLY 필드가 이미 18개월 히스토리를 포함하고 있어서 지금 1회만 돌려도 과거 데이터를 확보할 수 있습니다.



# ── 설정 ───────────────────────────────────────────────────
DB_PATH = "moneyland.db"
BASE_URL = "https://moneyland.co.kr/exports/tgtprc_{date}.html"

# ── HTML에서 STOCKS / MONTHLY 파싱 ─────────────────────────
def fetch_data(date_str: str) -> dict:
    """date_str: 'YYYYMMDD'"""
    url = BASE_URL.format(date=date_str)
    resp = requests.get(url, timeout=15)
    resp.raise_for_status()
    html = resp.text

    # STOCKS 추출
    m_stocks = re.search(r'const STOCKS = (\[.*?\]);', html, re.DOTALL)
    # MONTHLY 추출 (중첩 braces이므로 별도 처리)
    m_monthly_start = html.find('const MONTHLY = {')
    if m_monthly_start == -1:
        raise ValueError("MONTHLY not found")
    json_start = html.index('{', m_monthly_start)
    depth, json_end = 0, -1
    for i in range(json_start, len(html)):
        if html[i] == '{': depth += 1
        elif html[i] == '}':
            depth -= 1
            if depth == 0: json_end = i; break

    stocks  = json.loads(m_stocks.group(1))
    monthly = json.loads(html[json_start:json_end + 1])
    return {"stocks": stocks, "monthly": monthly}

# ── DB 초기화 ───────────────────────────────────────────────
def init_db(conn):
    conn.executescript("""
    CREATE TABLE IF NOT EXISTS tgt_stocks (
        fetch_date TEXT,
        itm_cd     TEXT,
        itm_nm     TEXT,
        brk_cnt    INTEGER,
        avg_tgt    INTEGER,
        min_tgt    INTEGER,
        max_tgt    INTEGER,
        cur_prc    INTEGER,
        upside     REAL,
        PRIMARY KEY (fetch_date, itm_cd)
    );
    CREATE TABLE IF NOT EXISTS tgt_monthly (
        itm_cd     TEXT,
        ym         TEXT,   -- 'YYYYMM'
        brk_cnt    INTEGER,
        avg_tgt    INTEGER,
        min_tgt    INTEGER,
        max_tgt    INTEGER,
        price      INTEGER,
        upside     REAL,
        PRIMARY KEY (itm_cd, ym)
    );
    """)

# ── DB 저장 ─────────────────────────────────────────────────
def save_to_db(conn, date_str: str, data: dict):
    # STOCKS (오늘 스냅샷)
    conn.executemany(
        "INSERT OR REPLACE INTO tgt_stocks VALUES (?,?,?,?,?,?,?,?,?)",
        [(date_str, s['itm_cd'], s['itm_nm'], s['brk_cnt'],
          s['avg_tgt'], s['min_tgt'], s['max_tgt'],
          s['cur_prc'], s['upside'])
         for s in data['stocks']]
    )
    # MONTHLY (히스토리, upsert)
    rows = []
    for cd, history in data['monthly'].items():
        for h in history:
            rows.append((h['itm_cd'], h['ym'], h['brk_cnt'],
                         h['avg_tgt'], h['min_tgt'], h['max_tgt'],
                         h['price'], h['upside']))
    conn.executemany(
        "INSERT OR REPLACE INTO tgt_monthly VALUES (?,?,?,?,?,?,?,?)", rows
    )
    conn.commit()
    print(f"[{date_str}] stocks={len(data['stocks'])}, monthly_rows={len(rows)}")

# ── 실행 ────────────────────────────────────────────────────
if __name__ == "__main__":
    conn = sqlite3.connect(DB_PATH)
    init_db(conn)

    # 오늘 날짜 1회 수집
    today = datetime.today().strftime("%Y%m%d")
    data  = fetch_data(today)
    save_to_db(conn, today, data)

    conn.close()
