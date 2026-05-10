"""
collector.py  ─  독립 실행형 데이터 수집기
============================================================
대상 테이블: daily_prices, supply, fundamentals
데이터 소스: pykrx (국내 주식 전용)

실행 방법
    cd C:/Users/tien7/source/repos/quant
               python collector.py               # 전체 incremental update
    특정 종목   python collector.py --tickers 005930 000660
    특정 테이블 python collector.py --tables prices supply
    전체 재수집 python collector.py --from 2020-01-01
    마스터 스킵 python collector.py --no-sync

주기 실행 예시 (Windows Task Scheduler / cron):
    python collector.py >> logs/collector.log 2>&1


수집 흐름
main()
 ├─ sync_stocks_master()       pykrx 전종목 → stocks upsert + 상폐 is_active=FALSE
 ├─ run_daily_prices()         OHLCV (adjusted=True) incremental
 ├─ run_supply()               기관·외인 순매수(주/금액) incremental
 └─ run_fundamentals()         일별 PER·PBR incremental (최근 1년 기본)    

 
설계 결정 사항
항목            결정                                근거
throttle        종목간 0.4s, 100종목마다 8s         pykrx 차단 방어
부족 컬럼       0으로 채움                          amount, roe, revenue 등
fundamentals    일별 PER/PBR만 수집, 분기 항목은 0  pykrx가 분기 재무를 미지원 — DART API 별도 필요
supply 컬럼     MultiIndex/단일 Index 모두 방어 처리pykrx 버전별 반환 구조 차이
incremental     ticker별 MAX(date) 1회 사전 로드    old_price_collector의 전체 skip 버그 재현 방지

[주의] fundamentals의 분기 항목(revenue, operating_income, net_income, roe)은 
pykrx가 제공하지 않아 0으로 채웁니다. 정확한 재무 데이터는 DART OpenAPI 연동이 필요합니다.

============================================================
test: 005930 삼성전자
============================================================
python collector.py --from 2025-05-07 --tables supply --tickers 005930

# 야간 실행 예시
Start-Process python -ArgumentList "collector.py --from 2020-01-01 --tables supply" -WindowStyle Hidden


python collector.py --from 2020-01-01 --tables prices --no-sync *>> logs/collector.log
python collector.py --from 2020-01-01 --tables prices --no-sync >> logs/collector.log 2>&1
python collector.py --from 2025-01-01 --tables supply --no-sync >> logs/collector.log 2>&1

최초수행: python collector.py --from 2025-01-01 --tables fundamentals --no-sync >> logs/collector.log 2>&1
증분실행: python collector.py --tables fundamentals --no-sync >> logs/collector.log 2>&1
Get-Content -Path "logs/collector.log" -Wait -Tail 10


TODO: 전체종목 순회시 매우 오랜 시간 소요 -> 증분 방식을 get_market_fundamental(date) 형태로 변경 필요?
============================================================
"""

import argparse
import json
import os
import sys
import time
import traceback
from datetime import date, timedelta
from pathlib import Path

import duckdb
import pandas as pd
from pykrx import stock as krx

# ──────────────────────────────────────────────────────────
#  설정
# ──────────────────────────────────────────────────────────

DB_PATH = Path(os.environ.get(
    "QUANT_DB",
    Path.home() / "AppData" / "Local" / "quant" / "quant.duckdb"
))

DEFAULT_START   = "2020-01-01"   # 전체 신규 수집 시작일
DELAY_SHORT     = 0.4            # 종목 간 기본 딜레이 (초)
DELAY_LONG      = 8.0            # 100종목마다 추가 휴식 (초)
DELAY_TABLE     = 3.0            # 테이블 전환 시 딜레이 (초)
BATCH_LOG       = 50             # 진행 로그 출력 단위

# ──────────────────────────────────────────────────────────
#  유틸
# ──────────────────────────────────────────────────────────

def log(msg: str):
    print(f"[{date.today().isoformat()} {time.strftime('%H:%M:%S')}] {msg}", flush=True)


def to_yyyymmdd(d: str) -> str:
    return d.replace("-", "")


def clamp_date(d: str, lo: str, hi: str) -> str:
    return max(lo, min(hi, d))


def safe_int(v, default: int = 0) -> int:
    try:
        r = int(v)
        return r if r == r else default   # NaN 방어
    except Exception:
        return default


def safe_float(v, default: float = 0.0) -> float:
    try:
        r = float(v)
        return r if r == r else default
    except Exception:
        return default


# ──────────────────────────────────────────────────────────
#  DB 연결
# ──────────────────────────────────────────────────────────

def open_db() -> duckdb.DuckDBPyConnection:
    DB_PATH.parent.mkdir(parents=True, exist_ok=True)
    return duckdb.connect(str(DB_PATH))


# ──────────────────────────────────────────────────────────
#  종목 목록
# ──────────────────────────────────────────────────────────

def get_active_tickers(conn: duckdb.DuckDBPyConnection) -> list[tuple[str, str]]:
    """(ticker, market='KP'|'KQ') 목록 반환."""
    rows = conn.execute(
        "SELECT ticker, market FROM stocks WHERE is_active = TRUE AND market IN ('KP','KQ') ORDER BY ticker"
    ).fetchall()
    return rows


# ──────────────────────────────────────────────────────────
#  last_date 캐시 (전체 1회 쿼리)
# ──────────────────────────────────────────────────────────

def load_last_dates(conn: duckdb.DuckDBPyConnection, table: str, date_col: str = "date") -> dict[str, str]:
    try:
        rows = conn.execute(
            f"SELECT ticker, MAX({date_col})::VARCHAR FROM {table} GROUP BY ticker"
        ).fetchall()
        return {r[0]: r[1] for r in rows}
    except Exception:
        return {}


# ──────────────────────────────────────────────────────────
#  로그 기록
# ──────────────────────────────────────────────────────────

def log_result(conn, ticker: str, ref_date: str, source: str, status: str, msg: str = ""):
    try:
        conn.execute(
            "INSERT INTO data_update_log (ticker, date, source, status, error_msg, run_at) "
            "VALUES (?, ?, ?, ?, ?, now())",
            [ticker, ref_date, source, status, msg[:500] if msg else None]
        )
    except Exception:
        pass   # 로그 실패는 무시


# ──────────────────────────────────────────────────────────
#  1. daily_prices  (OHLCV + adj_close + amount)
# ──────────────────────────────────────────────────────────

PYKRX_MARKET = {"KP": "KOSPI", "KQ": "KOSDAQ"}

def fetch_ohlcv(ticker: str, start: str, end: str) -> pd.DataFrame:
    """
    pykrx get_market_ohlcv_by_date (adjusted=True).
    반환 컬럼: ticker, date, open, high, low, close, adj_close, volume, amount
    부족한 컬럼은 0으로 채움.
    """
    raw = krx.get_market_ohlcv_by_date(
        to_yyyymmdd(start), to_yyyymmdd(end), ticker, adjusted=True
    )
    if raw is None or raw.empty:
        return pd.DataFrame()

    raw = raw.reset_index()

    # 컬럼 수에 따른 대응 (pykrx 버전별 차이)
    ncols = len(raw.columns)
    if ncols >= 8:
        raw.columns = ["date", "open", "high", "low", "close", "volume", "amount", "changes"] + list(raw.columns[8:])
    elif ncols == 7:
        raw.columns = ["date", "open", "high", "low", "close", "volume", "changes"]
        raw["amount"] = 0
    else:
        raw.columns = ["date", "open", "high", "low", "close"] + [f"_c{i}" for i in range(ncols - 5)]
        for c in ("volume", "amount"):
            if c not in raw.columns:
                raw[c] = 0

    raw["ticker"]    = ticker
    # adjusted=True: close 자체가 수정주가(액면분할·배당 반영).
    # PriceDownloadService(Yahoo)도 동일 기준(adj/raw 비율 환산)으로 통일됨.
    # 두 경로 모두 close = 수정주가, adj_close = close 동일값으로 저장.
    raw["adj_close"] = raw["close"]

    # CHECK 제약 조건 준수: open>0, low>0, close>0, adj_close>0
    raw = raw[(raw["open"] > 0) & (raw["low"] > 0) & (raw["close"] > 0)].copy()
    if raw.empty:
        return pd.DataFrame()

    raw["date"]      = pd.to_datetime(raw["date"]).dt.date
    raw["volume"]    = raw["volume"].fillna(0).astype("int64")
    raw["amount"]    = raw["amount"].fillna(0).astype("int64")

    return raw[["ticker", "date", "open", "high", "low", "close", "adj_close", "volume", "amount"]]


def upsert_daily_prices(conn, df: pd.DataFrame):
    conn.execute("""
        INSERT OR REPLACE INTO daily_prices
            (ticker, date, open, high, low, close, adj_close, volume, amount)
        SELECT ticker, date, open, high, low, close, adj_close, volume, amount
        FROM df
    """)


def run_daily_prices(conn, tickers: list[tuple[str, str]], force_start: str | None = None):
    log("=" * 60)
    log(f"[daily_prices] 시작  종목수={len(tickers)}")

    last_dates = {} if force_start else load_last_dates(conn, "daily_prices")
    today      = date.today().isoformat()
    skipped = updated = errors = 0

    for i, (ticker, market) in enumerate(tickers, 1):
        last = last_dates.get(ticker)
        start = force_start or (
            (date.fromisoformat(last) + timedelta(days=1)).isoformat() if last else DEFAULT_START
        )
        if start > today:
            skipped += 1
            continue

        try:
            df = fetch_ohlcv(ticker, start, today)
            if not df.empty:
                upsert_daily_prices(conn, df)
                updated += len(df)
                log_result(conn, ticker, today, "pykrx_ohlcv", "success")
        except Exception as e:
            errors += 1
            log_result(conn, ticker, today, "pykrx_ohlcv", "fail", traceback.format_exc(limit=3))
            log(f"  [ERR] {ticker}: {e}")

        _throttle(i, len(tickers))

    log(f"[daily_prices] 완료  rows={updated}  skip={skipped}  err={errors}")
    time.sleep(DELAY_TABLE)


# ──────────────────────────────────────────────────────────
#  2. supply  (기관·외인 순매수) — pykrx + KRX 로그인 세션
# ──────────────────────────────────────────────────────────
#
#  [Fact] KRX 정보데이터시스템은 로그인 세션 필수 (2024년 이후).
#         pykrx는 KRX_ID / KRX_PW 환경변수로 자동 로그인 및 세션 관리.
#
#  [설계]
#  - get_market_trading_volume_by_investor: 기관/외인 순매수(주) 일별 추이
#  - get_market_trading_value_by_investor:  기관/외인 순매수(금액) 일별 추이
#  - 종목당 2회 호출 (vol + val), 월 단위로 분할 → KRX 부하 감소
#  - incremental: ticker별 MAX(date) + 1일 부터 수집
#  - 주의: 환경변수 KRX_ID / KRX_PW 미설정 시 LOGOUT → 수집 실패

INVESTOR_INST    = "기관합계"
INVESTOR_FOREIGN = "외국인합계"


def _extract_investor_net(df: pd.DataFrame, investor: str) -> pd.Series:
    """투자자별 거래 DataFrame에서 순매수 시리즈 추출 (MultiIndex / 단일 Index 모두 대응)."""
    if df is None or df.empty:
        return pd.Series(dtype="int64")
    if isinstance(df.columns, pd.MultiIndex):
        cols = [c for c in df.columns if investor in str(c[0]) and "순매수" in str(c[1])]
        if cols:
            return df[cols[0]].fillna(0).astype("int64")
    else:
        cols = [c for c in df.columns if investor in str(c) and "순매수" in str(c)]
        if not cols:
            cols = [c for c in df.columns if investor in str(c)]
        if cols:
            return df[cols[0]].fillna(0).astype("int64")
    return pd.Series(dtype="int64")


def fetch_supply_pykrx(ticker: str, start: str, end: str) -> pd.DataFrame:
    """
    pykrx로 기관/외인 순매수(주+금액) 일별 수집.
    - get_market_trading_volume_by_date: 인덱스=날짜, 컬럼=기관합계/외국인합계 (순매수 주)
    - get_market_trading_value_by_date:  동일 구조 (순매수 금액)
    KRX_ID/KRX_PW 환경변수 필수.
    """
    s, e = to_yyyymmdd(start), to_yyyymmdd(end)
    try:
        vol = krx.get_market_trading_volume_by_date(s, e, ticker)
    except Exception:
        vol = None
    time.sleep(0.3)
    try:
        val = krx.get_market_trading_value_by_date(s, e, ticker)
    except Exception:
        val = None

    def col(df, kw):
        if df is None or df.empty:
            return None
        return next((c for c in df.columns if kw in str(c)), None)

    idx = vol.index if (vol is not None and not vol.empty) else (
          val.index if (val is not None and not val.empty) else None)
    if idx is None or len(idx) == 0:
        return pd.DataFrame()

    def get_series(df, kw):
        c = col(df, kw)
        if c:
            return df[c].reindex(idx).fillna(0).astype("int64")
        return pd.Series(0, index=idx, dtype="int64")

    result = pd.DataFrame({
        "ticker":             ticker,
        "date":              pd.to_datetime(idx).date,
        "inst_net_buy":      get_series(vol, INVESTOR_INST),
        "foreign_net_buy":   get_series(vol, INVESTOR_FOREIGN),
        "inst_net_amount":   get_series(val, INVESTOR_INST),
        "foreign_net_amount": get_series(val, INVESTOR_FOREIGN),
    })
    return result[["ticker", "date", "inst_net_buy", "foreign_net_buy",
                   "inst_net_amount", "foreign_net_amount"]]


def upsert_supply(conn, df: pd.DataFrame):
    conn.execute("""
        INSERT OR REPLACE INTO supply
            (ticker, date, inst_net_buy, foreign_net_buy, inst_net_amount, foreign_net_amount)
        SELECT ticker, date, inst_net_buy, foreign_net_buy, inst_net_amount, foreign_net_amount
        FROM df
    """)


def run_supply(conn, tickers: list[tuple[str, str]], force_start: str | None = None):
    """
    pykrx get_market_trading_volume/value_by_investor 로 supply 수집.
    KRX_ID / KRX_PW 환경변수 설정 필수.
    월 단위로 분할하여 수집 (KRX 응답 크기 제한 대응).
    """
    log("=" * 60)
    log(f"[supply] 시작  종목수={len(tickers)}  소스=pykrx(KRX 로그인)")

    last_dates = {} if force_start else load_last_dates(conn, "supply")
    today      = date.today().isoformat()
    skipped = updated = errors = 0

    for i, (ticker, _) in enumerate(tickers, 1):
        last  = last_dates.get(ticker)
        start = force_start or (
            (date.fromisoformat(last) + timedelta(days=1)).isoformat() if last else DEFAULT_START
        )
        if start > today:
            skipped += 1
            continue

        # 월 단위 분할 수집
        for p_start, p_end in _month_ranges(start, today):
            try:
                df = fetch_supply_pykrx(ticker, p_start, p_end)
                if not df.empty:
                    upsert_supply(conn, df)
                    updated += len(df)
                log_result(conn, ticker, p_end, "pykrx_supply", "success")
            except Exception as e:
                errors += 1
                log_result(conn, ticker, p_end, "pykrx_supply", "fail",
                           traceback.format_exc(limit=3))
                log(f"  [ERR] {ticker} {p_start}~{p_end}: {e}")
            time.sleep(DELAY_SHORT)

        _throttle(i, len(tickers))

    log(f"[supply] 완료  rows={updated}  skip={skipped}  err={errors}")
    time.sleep(DELAY_TABLE)


def _month_ranges(start: str, end: str) -> list[tuple[str, str]]:
    """start~end 를 1개월 단위 (start, end) 튜플 리스트로 분할."""
    result = []
    cur = date.fromisoformat(start)
    fin = date.fromisoformat(end)
    while cur <= fin:
        if cur.month == 12:
            m_end = date(cur.year + 1, 1, 1) - timedelta(days=1)
        else:
            m_end = date(cur.year, cur.month + 1, 1) - timedelta(days=1)
        p_end = min(m_end, fin)
        result.append((cur.isoformat(), p_end.isoformat()))
        cur = p_end + timedelta(days=1)
    return result


# ──────────────────────────────────────────────────────────
#  3. fundamentals  (재무 데이터)
# ──────────────────────────────────────────────────────────
#
#  [설계 변경] ticker 순회 → 날짜 순회 (날짜별 전종목 스냅샷)
#
#  변경 전: for ticker in tickers → get_market_fundamental_by_date(ticker)
#            API 호출수 = 종목수(~2,500) × 기간(일)
#
#  변경 후: for d in missing_dates → get_market_fundamental(date, market)
#            API 호출수 = 누락 날짜수 × 2(KOSPI+KOSDAQ)
#            증분 실행 시 = 1~5일 × 2 = 2~10회
#
#  [주의] get_market_fundamental(date) 는 ticker 인자 없이
#         해당 날짜의 전종목 PER/PBR/EPS/BPS/DPS 를 DataFrame으로 반환.
#         인덱스 = ticker (종목코드)
#
#  pykrx는 분기 재무 데이터를 직접 제공하지 않음.
#  분기 EPS·ROE·매출은 pykrx 미지원 → 0으로 채워 구조 유지.


def _trading_dates(start: str, end: str) -> list[str]:
    """
    start~end 범위의 KRX 개장일 목록 반환.
    KOSPI 지수('1028') OHLCV를 이용해 실제 개장일만 추출.
    """
    try:
        raw = krx.get_market_ohlcv_by_date(
            to_yyyymmdd(start), to_yyyymmdd(end), "1028"  # KOSPI 지수 코드
        )
        if raw is None or raw.empty:
            return []
        return [d.strftime("%Y-%m-%d") for d in raw.index]
    except Exception:
        # fallback: 주말 제외 달력일 (공휴일 포함될 수 있음)
        result = []
        cur = date.fromisoformat(start)
        fin = date.fromisoformat(end)
        while cur <= fin:
            if cur.weekday() < 5:
                result.append(cur.isoformat())
            cur += timedelta(days=1)
        return result


def fetch_market_fundamentals_snapshot(target_date: str, market: str) -> pd.DataFrame:
    """
    pykrx get_market_fundamental(date, market) → 전종목 스냅샷 1회 호출.
    반환: 전종목 PER/PBR/EPS/BPS/DPS (인덱스=ticker).

    market: 'KOSPI' | 'KOSDAQ'
    """
    for attempt in range(3):
        try:
            raw = krx.get_market_fundamental(to_yyyymmdd(target_date), market=market)
            if raw is None or raw.empty:
                return pd.DataFrame()
            break
        except json.JSONDecodeError:
            if attempt < 2:
                time.sleep(2.0 * (attempt + 1))
                continue
            return pd.DataFrame()
        except Exception:
            raise

    raw = raw.reset_index()

    # 첫 컬럼(ticker) 정규화
    first_col = raw.columns[0]
    raw = raw.rename(columns={first_col: "ticker"})

    # 수치 컬럼 대소문자 정규화
    col_map = {}
    for c in raw.columns:
        cl = str(c).upper().strip()
        if cl == "PER":   col_map[c] = "per"
        elif cl == "PBR": col_map[c] = "pbr"
        elif cl == "EPS": col_map[c] = "eps"
        elif cl == "BPS": col_map[c] = "bps"
        elif cl == "DPS": col_map[c] = "dps"
    raw = raw.rename(columns=col_map)

    out = pd.DataFrame({
        "ticker":           raw["ticker"].astype(str),
        "report_date":      date.fromisoformat(target_date),
        "announce_date":    None,
        "fiscal_quarter":   "DAILY",
        "eps":              pd.to_numeric(raw.get("eps", 0), errors="coerce").fillna(0),
        "per":              pd.to_numeric(raw.get("per", 0), errors="coerce").fillna(0),
        "pbr":              pd.to_numeric(raw.get("pbr", 0), errors="coerce").fillna(0),
        "roe":              0.0,
        "revenue":          0,
        "operating_income": 0,
        "net_income":       0,
        "debt_ratio":       0.0,
    })
    # 6자리 숫자 종목코드만 (지수·ETF 혼입 방어)
    out = out[out["ticker"].str.match(r"^\d{6}$")].copy()
    return out[["ticker", "report_date", "announce_date", "fiscal_quarter",
                "eps", "per", "pbr", "roe", "revenue", "operating_income",
                "net_income", "debt_ratio"]]


def upsert_fundamentals(conn, df: pd.DataFrame):
    conn.execute("""
        INSERT OR REPLACE INTO fundamentals
            (ticker, report_date, announce_date, fiscal_quarter,
             eps, per, pbr, roe, revenue, operating_income, net_income, debt_ratio)
        SELECT ticker, report_date, announce_date, fiscal_quarter,
               eps, per, pbr, roe, revenue, operating_income, net_income, debt_ratio
        FROM df
    """)


def _load_existing_fundamental_dates(conn) -> set[str]:
    """fundamentals 테이블에 이미 수집된 날짜 집합 반환."""
    try:
        rows = conn.execute(
            "SELECT DISTINCT report_date::VARCHAR FROM fundamentals"
        ).fetchall()
        return {r[0] for r in rows}
    except Exception:
        return set()


def run_fundamentals(conn, tickers: list[tuple[str, str]], force_start: str | None = None):
    """
    [변경] ticker 순회 → 날짜 순회 방식.
    누락된 개장일을 구한 뒤 날짜별 전종목 스냅샷을 1회 호출로 수집.

    증분 실행: fundamentals.MAX(report_date)+1일 이후 누락일만 처리.
    force_start 지정 시: 해당일 이후 전체 재수집.
    tickers 인자는 시그니처 호환성을 위해 유지하나 사용하지 않음.
    """
    log("=" * 60)
    log("[fundamentals] 시작  방식=날짜순회(전종목스냅샷)")

    today = date.today().isoformat()

    if force_start:
        start = force_start
        existing_dates: set[str] = set()
    else:
        try:
            row = conn.execute(
                "SELECT MAX(report_date)::VARCHAR FROM fundamentals"
            ).fetchone()
            last_global = row[0] if row and row[0] else None
        except Exception:
            last_global = None

        start = (
            (date.fromisoformat(last_global) + timedelta(days=1)).isoformat()
            if last_global else DEFAULT_START
        )
        existing_dates = _load_existing_fundamental_dates(conn)

    if start > today:
        log("[fundamentals] 이미 최신 상태 -- skip")
        return

    # 실제 개장일 목록 조회 (1회 API 호출)
    all_dates = _trading_dates(start, today)
    missing   = [d for d in all_dates if d not in existing_dates]

    log(f"  수집 대상: {len(missing)}일  ({start} ~ {today})")
    if not missing:
        log("[fundamentals] 누락 날짜 없음 -- skip")
        return

    updated = errors = 0

    for i, target_date in enumerate(missing, 1):
        rows_today = 0
        for pykrx_mkt in ("KOSPI", "KOSDAQ"):
            try:
                df = fetch_market_fundamentals_snapshot(target_date, pykrx_mkt)
                if not df.empty:
                    upsert_fundamentals(conn, df)
                    rows_today += len(df)
                time.sleep(DELAY_SHORT)
            except Exception as e:
                errors += 1
                log_result(conn, "ALL", target_date,
                           f"pykrx_fundamental_{pykrx_mkt}", "fail",
                           traceback.format_exc(limit=3))
                log(f"  [ERR] {target_date} {pykrx_mkt}: {e}")

        updated += rows_today
        if i % BATCH_LOG == 0 or i == len(missing):
            log(f"  진행: {i}/{len(missing)}  누적rows={updated}")
        time.sleep(DELAY_SHORT)

    log(f"[fundamentals] 완료  rows={updated}  날짜={len(missing)}  err={errors}")


# ──────────────────────────────────────────────────────────
#  공통 throttle
# ──────────────────────────────────────────────────────────

def _throttle(i: int, total: int):
    if i % BATCH_LOG == 0:
        log(f"  진행: {i}/{total}")
    if i % 100 == 0:
        log(f"  --- 100종목 완료, {DELAY_LONG}초 휴식 ---")
        time.sleep(DELAY_LONG)
    else:
        time.sleep(DELAY_SHORT)


# ──────────────────────────────────────────────────────────
#  stocks 마스터 동기화 (신규 상장 반영)
# ──────────────────────────────────────────────────────────

def sync_stocks_master(conn):
    """
    pykrx에서 현재 KOSPI+KOSDAQ 종목 목록을 가져와 stocks 테이블 upsert.
    신규 상장 종목 자동 추가. 상폐 종목은 is_active=FALSE 처리.
    """
    log("[stocks] 마스터 동기화 시작")
    today = date.today().strftime("%Y%m%d")

    rows = []
    for pykrx_mkt, db_mkt in [("KOSPI", "KP"), ("KOSDAQ", "KQ")]:
        try:
            tickers = krx.get_market_ticker_list(today, market=pykrx_mkt)
            for t in tickers:
                try:
                    name = krx.get_market_ticker_name(t)
                    rows.append({"ticker": t, "name": name, "market": db_mkt})
                    time.sleep(0.02)
                except Exception:
                    pass
        except Exception as e:
            log(f"  [WARN] {pykrx_mkt} 목록 조회 실패: {e}")

    if not rows:
        log("[stocks] 종목 목록 없음 — 마스터 동기화 skip")
        return

    df = pd.DataFrame(rows)
    live_tickers = set(df["ticker"])

    # upsert: 이름 갱신, 신규 추가 (rating·is_active 기본값 유지)
    for _, r in df.iterrows():
        conn.execute("""
            INSERT INTO stocks (ticker, name, market, security_type, updated_at)
            VALUES (?, ?, ?, 'stock', now())
            ON CONFLICT (ticker) DO UPDATE SET
                name       = excluded.name,
                updated_at = now()
        """, [r["ticker"], r["name"], r["market"]])

    # 현재 목록에 없는 종목 → is_active=FALSE
    existing = {r[0] for r in conn.execute(
        "SELECT ticker FROM stocks WHERE market IN ('KP','KQ') AND is_active = TRUE"
    ).fetchall()}
    delisted = existing - live_tickers
    if delisted:
        for t in delisted:
            conn.execute(
                "UPDATE stocks SET is_active=FALSE, updated_at=now() WHERE ticker=?", [t]
            )
        log(f"  상폐 처리: {len(delisted)}종목")

    log(f"[stocks] 동기화 완료  활성={len(live_tickers)}  상폐처리={len(delisted)}")


# ──────────────────────────────────────────────────────────
#  진입점
# ──────────────────────────────────────────────────────────

def parse_args():
    p = argparse.ArgumentParser(description="pykrx 데이터 수집기")
    p.add_argument("--tickers", nargs="*", help="특정 ticker만 처리 (미지정시 전체)")
    p.add_argument("--from",    dest="from_date", default=None,
                   help="강제 시작일 YYYY-MM-DD (미지정시 incremental)")
    p.add_argument("--tables",  nargs="*",
                   default=["prices", "supply", "fundamentals"],
                   choices=["prices", "supply", "fundamentals"],
                   help="수집할 테이블 선택 (기본: 전체)")
    p.add_argument("--no-sync", action="store_true",
                   help="stocks 마스터 동기화 skip")
    return p.parse_args()


def main():
    args = parse_args()
    t0   = time.time()

    log(f"수집기 시작  DB={DB_PATH}")
    if not DB_PATH.exists():
        log(f"[ERROR] DB 파일 없음: {DB_PATH}")
        sys.exit(1)

    conn = open_db()

    # 1. stocks 마스터 동기화
    if not args.no_sync:
        sync_stocks_master(conn)

    # 2. 대상 종목 결정
    if args.tickers:
        # 지정 ticker만 (market 정보는 DB에서 조회)
        ph = ",".join(f"'{t}'" for t in args.tickers)
        rows = conn.execute(
            f"SELECT ticker, market FROM stocks WHERE ticker IN ({ph}) AND is_active = TRUE"
        ).fetchall()
    else:
        rows = get_active_tickers(conn)

    if not rows:
        log("[WARN] 처리할 종목 없음")
        conn.close()
        return

    log(f"처리 대상: {len(rows)}종목  테이블={args.tables}  from={args.from_date or '(incremental)'}")

    # 3. 테이블별 수집
    if "prices" in args.tables:
        run_daily_prices(conn, rows, force_start=args.from_date)

    if "supply" in args.tables:
        run_supply(conn, rows, force_start=args.from_date)

    if "fundamentals" in args.tables:
        run_fundamentals(conn, rows, force_start=args.from_date)

    conn.close()
    elapsed = time.time() - t0
    log(f"전체 완료  소요시간={elapsed/60:.1f}분")


if __name__ == "__main__":
    main()
