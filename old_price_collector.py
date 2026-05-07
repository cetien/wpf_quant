"""
ingestion/price_collector.py
일봉 OHLCV 수집: pykrx (KR 국내) + yfinance (글로벌 보완)
- 거래대금(amount): pykrx 제공 / yfinance 미제공
- 증분 업데이트: ticker별 last_date 이후 구간만 수집
- Rate Limit 대응: tenacity 기반 exponential backoff
"""
from datetime import date, timedelta
from typing import List, Optional

import pandas as pd
import time

from common.config import cfg
from common.logger import get_logger
from storage.db_manager import DuckDBManager
from storage.parquet_store import save_prices

log = get_logger(__name__)

try:
    from tenacity import retry, stop_after_attempt, wait_exponential
    HAS_TENACITY = True
except ImportError:
    HAS_TENACITY = False
    log.warning("tenacity 미설치. 단순 재시도 로직 사용.")


def _retry_decorator():
    if HAS_TENACITY:
        return retry(
            stop=stop_after_attempt(cfg.ingestion.retry_max),
            wait=wait_exponential(
                multiplier=cfg.ingestion.retry_backoff, min=1, max=30
            ),
        )
    return lambda f: f  # no-op


class PriceCollector:
    """
    KR 일봉 OHLCV + 거래대금 수집 (pykrx 우선, yfinance 보완).
    """

    def __init__(self, db: DuckDBManager) -> None:
        self.db = db
        self._check_dependencies()

    def _check_dependencies(self):
        try:
            import pykrx  # noqa
        except ImportError:
            log.warning("pykrx 미설치: pip install pykrx  ← 거래대금·수급 수집 불가")
        try:
            import yfinance  # noqa
        except ImportError:
            log.warning("yfinance 미설치: pip install yfinance")

    # ── 종목 목록 조회 ────────────────────────────────────────────────────────

    def fetch_stock_list(self) -> pd.DataFrame:
        """
        pykrx에서 KOSPI + KOSDAQ 전체 종목 목록 수집.
        반환: [ticker, name, market]
        섹터/산업은 별도 소스(FinanceDataReader 등) 필요.
        """
        try:
            from pykrx import stock
            today = date.today().strftime("%Y%m%d")
            rows = []
            for market in ["KOSPI", "KOSDAQ"]:
                tickers = stock.get_market_ticker_list(today, market=market)
                for t in tickers:
                    name = stock.get_market_ticker_name(t)
                    rows.append({"ticker": t, "name": name, "market": market})
            df = pd.DataFrame(rows)
            # sector 컬럼 없음: stock_sector_map 테이블로 분리 관리
            log.info(f"종목 목록 수집: {len(df)}개")
            return df
        except Exception as e:
            log.error(f"fetch_stock_list 실패: {e}")
            return pd.DataFrame(columns=["ticker", "name", "market"])

    def update_stock_master(self) -> int:
        """stocks 테이블 최신 상태 동기화."""
        df = self.fetch_stock_list()
        if df.empty:
            return 0
        self.db.upsert_dataframe(df, "stocks", pk_cols=["ticker"])
        log.info("stocks 테이블 업데이트 완료.")
        return len(df)

    # ── 일봉 수집 (pykrx) ────────────────────────────────────────────────────

    def fetch_daily_ohlcv_pykrx(
        self,
        ticker: str,
        start: str,
        end: str,
    ) -> pd.DataFrame:
        """단일 종목 일봉 수집 (거래대금 포함)."""
        try:
            from pykrx import stock
            raw = stock.get_market_ohlcv_by_date(
                start.replace("-", ""),
                end.replace("-", ""),
                ticker,
            )
            if raw.empty:
                return pd.DataFrame()

            raw = raw.reset_index()
            # pykrx 반환 컬럼 수에 따라 대응 (거래대금 'amount' 포함 여부 체크)
            if len(raw.columns) == 8:
                raw.columns = ["date", "open", "high", "low", "close", "volume", "amount", "changes"]
            elif len(raw.columns) == 7:
                # 거래대금이 빠진 경우: [date, open, high, low, close, volume, changes]
                raw.columns = ["date", "open", "high", "low", "close", "volume", "changes"]
                raw["amount"] = 0  # 부족한 컬럼은 0으로 채워 데이터 구조 유지

            raw["ticker"] = ticker
            raw["adj_close"] = raw["close"]  # pykrx 수정주가: 별도 API 필요
            raw["date"] = pd.to_datetime(raw["date"])

            # DB CHECK 제약 조건(open > 0, close > 0) 준수를 위해 0인 데이터 필터링
            # 거래 정지 종목 등 가격이 0으로 들어오는 경우를 제외합니다.
            raw = raw[(raw["open"] > 0) & (raw["close"] > 0)].copy()

            return raw[["ticker", "date", "open", "high", "low", "close", "adj_close", "volume", "amount"]]
        except Exception as e:
            log.error(f"pykrx 수집 실패 [{ticker}]: {e}")
            return pd.DataFrame()

    # ── ticker별 first_date / last_date 사전 로드 ────────────────────────────

    def _load_ticker_first_dates(self) -> dict:
        """
        daily_prices 테이블에서 ticker별 최초 날짜를 1회 쿼리로 로드.
        반환: {ticker: "YYYY-MM-DD"} — 수집 이력 없는 종목은 포함되지 않음.
        """
        try:
            df = self.db.query(
                "SELECT ticker, MIN(date)::VARCHAR AS first_date FROM daily_prices GROUP BY ticker"
            )
            if df.empty:
                return {}
            return dict(zip(df["ticker"], df["first_date"]))
        except Exception as e:
            log.warning(f"_load_ticker_first_dates 실패: {e}")
            return {}

    def _load_ticker_last_dates(self) -> dict:
        """
        daily_prices 테이블에서 ticker별 최신 날짜를 1회 쿼리로 로드.
        반환: {ticker: "YYYY-MM-DD"} — 수집 이력 없는 종목은 포함되지 않음.
        """
        try:
            df = self.db.query(
                "SELECT ticker, MAX(date)::VARCHAR AS last_date FROM daily_prices GROUP BY ticker"
            )
            if df.empty:
                return {}
            return dict(zip(df["ticker"], df["last_date"]))
        except Exception as e:
            log.warning(f"_load_ticker_last_dates 실패 (전체 신규 수집으로 진행): {e}")
            return {}

    # ── 과거 데이터 소급 수집 (backward) ─────────────────────────────────────

    def backfill_daily_prices(
        self,
        tickers: Optional[List[str]] = None,
        start: Optional[str] = None,
    ) -> int:
        """
        ticker별 first_date 이전 구간을 소급 수집 (backward fill).

        - start: 소급 시작일. 미지정 시 cfg.ingestion.default_start_date 사용.
        - 수집 이력 없는 종목은 default_start_date ~ 오늘 전체 수집.
        - 이미 first_date가 start 이전인 종목은 skip.
        """
        if tickers is None:
            df_stocks = self.db.query("SELECT ticker FROM stocks WHERE is_active = TRUE")
            tickers = df_stocks["ticker"].tolist()

        start = start or cfg.ingestion.default_start_date

        ticker_first_dates = self._load_ticker_first_dates()

        log.info(f"일봉 소급 수집 시작: {len(tickers)}개 종목, start={start}")
        all_rows = []
        skipped = 0

        for i, ticker in enumerate(tickers):
            first_date = ticker_first_dates.get(ticker)
            if first_date:
                if first_date <= start:
                    log.debug(f"[{ticker}] 이미 {first_date}까지 수집됨. skip.")
                    skipped += 1
                    continue
                # first_date 이전 구간만 수집
                end_for_ticker = (
                    pd.Timestamp(first_date) - timedelta(days=1)
                ).strftime("%Y-%m-%d")
            else:
                # 수집 이력 없음 → 전체 구간
                end_for_ticker = date.today().strftime("%Y-%m-%d")

            if start > end_for_ticker:
                skipped += 1
                continue

            df = self.fetch_daily_ohlcv_pykrx(ticker, start, end_for_ticker)
            if not df.empty:
                all_rows.append(df)
                log.debug(f"[{ticker}] {len(df)}행 소급 수집 ({start} ~ {end_for_ticker})")

            time.sleep(cfg.ingestion.pykrx_delay_sec)

            if (i + 1) % 100 == 0:
                log.info(f"  진행: {i+1}/{len(tickers)} (수집중={len(all_rows)}건 누적, skip={skipped})")

        if all_rows:
            combined = pd.concat(all_rows, ignore_index=True)
            self.db.upsert_dataframe(combined, "daily_prices", pk_cols=["ticker", "date"])
            save_prices(combined, category="prices")
            log.info(f"일봉 소급 수집 완료: {len(combined)}행 적재, {skipped}종목 skip")
            updated_tickers = combined["ticker"].unique().tolist()
            self.db.refresh_stock_cache(tickers=updated_tickers)
            return len(combined)
        else:
            log.info(f"소급 수집 데이터 없음. {skipped}종목 모두 skip.")
            return 0

    # ── 증분 업데이트 ─────────────────────────────────────────────────────────

    def incremental_update_daily_prices(
        self,
        tickers: Optional[List[str]] = None,
        end: Optional[str] = None,
    ) -> int:
        """
        전체 종목(또는 지정 종목) 일봉 증분 업데이트.
        ticker별 last_date 이후 구간만 수집 → DB + Parquet 모두 적재.

        수정 이력:
        - v1: 전체 테이블 MAX(date) 1회 조회 → 1번째 종목 수집 후 나머지 종목 skip 버그
        - v2: ticker별 MAX(date) 사전 로드 → 종목별 독립적 증분 구간 계산
        """
        if tickers is None:
            df_stocks = self.db.query("SELECT ticker FROM stocks WHERE is_active = TRUE")
            tickers = df_stocks["ticker"].tolist()

        end = end or date.today().strftime("%Y-%m-%d")

        # ★ 핵심 수정: ticker별 last_date를 1회 쿼리로 사전 로드
        ticker_last_dates = self._load_ticker_last_dates()

        log.info(f"일봉 증분 수집 시작: {len(tickers)}개 종목, end={end}")
        all_rows = []
        skipped = 0

        for i, ticker in enumerate(tickers):
            # ★ 종목별 독립적 start 계산
            last_date = ticker_last_dates.get(ticker)
            if last_date:
                start = (
                    pd.Timestamp(last_date) + timedelta(days=1)
                ).strftime("%Y-%m-%d")
            else:
                start = cfg.ingestion.default_start_date

            # ★ early skip은 전체 함수가 아닌 해당 종목만
            if start > end:
                log.debug(f"[{ticker}] 이미 최신 ({last_date}). skip.")
                skipped += 1
                continue

            df = self.fetch_daily_ohlcv_pykrx(ticker, start, end)
            if not df.empty:
                all_rows.append(df)
                log.debug(f"[{ticker}] {len(df)}행 수집 ({start} ~ {end})")

            # Rate Limit 방지
            time.sleep(cfg.ingestion.pykrx_delay_sec)

            if (i + 1) % 100 == 0:
                log.info(f"  진행: {i+1}/{len(tickers)} (수집중={len(all_rows)}건 누적, skip={skipped})")

        if all_rows:
            combined = pd.concat(all_rows, ignore_index=True)
            self.db.upsert_dataframe(combined, "daily_prices", pk_cols=["ticker", "date"])
            save_prices(combined, category="prices")
            log.info(f"일봉 수집 완료: {len(combined)}행 적재, {skipped}종목 skip")

            # ★ stock_cache 갱신: 수집된 종목만 대상으로 상승률 재계산
            updated_tickers = combined["ticker"].unique().tolist()
            self.db.refresh_stock_cache(tickers=updated_tickers)

            return len(combined)
        else:
            log.info(f"수집된 데이터 없음. {skipped}종목 모두 최신 상태.")
            return 0
