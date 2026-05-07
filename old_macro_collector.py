"""
ingestion/macro_collector.py
글로벌 매크로 지표 수집 (yfinance)
SOX, S&P500, NASDAQ, KOSPI, KOSDAQ, USD/KRW, 미국10년국채
"""
from datetime import date, timedelta
from typing import Optional
import time

import pandas as pd

from common.config import cfg
from common.logger import get_logger
from storage.db_manager import DuckDBManager
from storage.parquet_store import save_prices

log = get_logger(__name__)

# yfinance 티커 → indicator_code 매핑
TICKER_MAP = {
    "^SOX":   "SOX",
    "^GSPC":  "SP500",
    "^IXIC":  "NASDAQ",
    "^KS11":  "KOSPI",
    "^KQ11":  "KOSDAQ",
    "KRW=X":  "USD_KRW",
    "^TNX":   "US10Y",
}


class MacroCollector:
    """글로벌 매크로 지표 수집."""

    def __init__(self, db: DuckDBManager) -> None:
        self.db = db

    def fetch_macro_yfinance(
        self,
        start: str,
        end: str,
    ) -> pd.DataFrame:
        try:
            import yfinance as yf
            tickers = list(TICKER_MAP.keys())
            raw = yf.download(
                tickers,
                start=start,
                end=end,
                auto_adjust=True,
                progress=False,
            )
            close = raw["Close"] if "Close" in raw.columns else raw.xs("Close", axis=1, level=0)

            rows = []
            for yf_ticker, code in TICKER_MAP.items():
                if yf_ticker not in close.columns:
                    continue
                s = close[yf_ticker].dropna()
                df_tmp = pd.DataFrame({
                    "indicator_code": code,
                    "date":           s.index,
                    "value":          s.values,
                })
                df_tmp["change_rate"] = df_tmp["value"].pct_change() * 100
                rows.append(df_tmp)

            if not rows:
                return pd.DataFrame()

            df = pd.concat(rows, ignore_index=True)
            df["date"] = pd.to_datetime(df["date"])
            return df[["indicator_code", "date", "value", "change_rate"]]

        except Exception as e:
            log.error(f"macro 수집 실패: {e}")
            return pd.DataFrame()

    def incremental_update_macro(
        self,
        end: Optional[str] = None,
    ) -> None:
        end = end or date.today().strftime("%Y-%m-%d")
        last_date = self.db.get_last_date("macro_indicators")

        if last_date:
            # 등락률 계산을 위해 마지막 데이터 날짜(기준점)부터 수집 시작
            start = last_date
            if (pd.Timestamp(last_date) + timedelta(days=1)).strftime("%Y-%m-%d") > end:
                log.info("매크로 데이터 이미 최신.")
                return
        else:
            start = cfg.ingestion.default_start_date

        log.info(f"매크로 수집: {start} ~ {end}")
        df = self.fetch_macro_yfinance(start, end)

        if not df.empty:
            self.db.upsert_dataframe(df, "macro_indicators", pk_cols=["indicator_code", "date"])
            save_prices(df, category="macro")
            log.info(f"매크로 수집 완료: {len(df)}행")
