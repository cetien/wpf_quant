import re, json, calendar, requests
import duckdb
from datetime import date, datetime

DB_PATH  = "stock_tp.duckdb"
BASE_URL = "https://moneyland.co.kr/exports/tgtprc_{date}.html"

def fetch_monthly(date_str: str) -> list[dict]:
    url = BASE_URL.format(date=date_str)
    headers = {"Accept-Encoding": "gzip, deflate, br"}  # zstd 제외
    html = requests.get(url, headers=headers, timeout=15).text

    start = html.find('const MONTHLY = {')
    js = html.index('{', start)
    depth = 0
    for i in range(js, len(html)):
        if html[i] == '{': depth += 1
        elif html[i] == '}':
            depth -= 1
            if depth == 0:
                monthly = json.loads(html[js:i+1])
                break

    def ym_to_monthend(ym):
        y, m = int(ym[:4]), int(ym[4:])
        return date(y, m, calendar.monthrange(y, m)[1])

    rows = []
    for cd, history in monthly.items():
        for h in history:
            if h.get("avg_tgt") is None:
                continue
            rows.append({
                "ticker":    h["itm_cd"],
                "date":      ym_to_monthend(h["ym"]),
                "tp_avg":    float(h["avg_tgt"]),
                "tp_min":    float(h["min_tgt"])  if h.get("min_tgt")  is not None else None,
                "tp_max":    float(h["max_tgt"])  if h.get("max_tgt")  is not None else None,
                "tp_cnt":    int(h["brk_cnt"])    if h.get("brk_cnt")  is not None else None,
                "tp_upside": float(h["upside"])   if h.get("upside")   is not None else None,
                "price":     float(h["price"])    if h.get("price")    is not None else None,
            })
    return rows

def save(rows: list[dict]):
    con = duckdb.connect(DB_PATH)
    con.execute("""
        CREATE TABLE IF NOT EXISTS stock_tp_consensus (
            ticker     TEXT      NOT NULL,
            date       DATE      NOT NULL,
            source     TEXT      NOT NULL DEFAULT 'moneyland',
            tp_avg     DOUBLE    NOT NULL,
            tp_min     DOUBLE,
            tp_max     DOUBLE,
            tp_cnt     INTEGER,
            tp_upside  DOUBLE,
            price      DOUBLE,
            fetched_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (ticker, date, source)
        )
    """)
    con.executemany("""
        INSERT INTO stock_tp_consensus
            (ticker, date, tp_avg, tp_min, tp_max, tp_cnt, tp_upside, price)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT (ticker, date, source) DO UPDATE SET
            tp_avg     = excluded.tp_avg,
            tp_min     = excluded.tp_min,
            tp_max     = excluded.tp_max,
            tp_cnt     = excluded.tp_cnt,
            tp_upside  = excluded.tp_upside,
            price      = excluded.price,
            fetched_at = CURRENT_TIMESTAMP
    """, [(r["ticker"], r["date"], r["tp_avg"], r["tp_min"],
           r["tp_max"], r["tp_cnt"], r["tp_upside"], r["price"]) for r in rows])
    con.close()
    print(f"saved {len(rows)} rows")

if __name__ == "__main__":
    today = datetime.today().strftime("%Y%m%d")
    rows  = fetch_monthly(today)
    save(rows)
