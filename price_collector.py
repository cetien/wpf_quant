import time
import pandas as pd
import duckdb
from datetime import date, timedelta
from pykrx import stock

# [Fact]: 마켓 코드 매핑 (New Schema 기준) [2026-05-06, source: 3]
MARKET_MAP = {"KP": "KOSPI", "KQ": "KOSDAQ"}

class DailyPriceDownloader:
    def __init__(self, db_path: str):
        self.db_path = db_path
        self.conn = duckdb.connect(self.db_path)

    def _get_target_stocks(self):
        # [Fact]: stocks 테이블에서 활성 상태인 국내 종목만 추출
        query = "SELECT ticker, market FROM stocks WHERE is_active = TRUE AND market IN ('KP', 'KQ')"
        return self.conn.query(query).df()

    def _get_last_date(self, ticker: str):
        # [Fact]: daily_prices 테이블에서 종목별 마지막 수집일 조회
        query = f"SELECT MAX(date) FROM daily_prices WHERE ticker = '{ticker}'"
        result = self.conn.query(query).fetchone()
        return result[0] if result and result[0] else None

    def run(self):
        targets = self._get_target_stocks()
        total = len(targets)
        
        for i, row in targets.iterrows():
            ticker, market = row['ticker'], row['market']
            pykrx_market = MARKET_MAP[market]
            
            last_date = self._get_last_date(ticker)
            start_date = (last_date + timedelta(days=1)) if last_date else date(2020, 1, 1)
            end_date = date.today()

            if start_date > end_date:
                continue

            try:
                # [Fact]: pykrx를 이용한 일봉 및 거래대금 수집
                df = stock.get_market_ohlcv_by_date(
                    start_date.strftime("%Y%m%d"), 
                    end_date.strftime("%Y%m%d"), 
                    ticker
                )

                if not df.empty:
                    df = df.reset_index()
                    df.columns = ['date', 'open', 'high', 'low', 'close', 'volume', 'amount', 'changes']
                    df['ticker'] = ticker
                    df['adj_close'] = df['close'] # [Inference]: 수정주가 보정 로직 부재 (Error: 15%)

                    # [Fact]: daily_prices 스키마 제약 조건 준수 (low > 0, amount 포함)
                    # 필터링: open, low, close가 0보다 큰 데이터만 유지
                    valid_df = df[(df['open'] > 0) & (df['low'] > 0) & (df['close'] > 0)].copy()
                    
                    if not valid_df.empty:
                        # [Fact]: DuckDB append를 통한 적재[cite: 3]
                        self.conn.append('daily_prices', valid_df[['ticker', 'date', 'open', 'high', 'low', 'close', 'adj_close', 'volume', 'amount']])

                # [Logic]: 지연 전략 - 기본 0.5초, 100단위 5초 추가 휴식
                time.sleep(0.5)
                if (i + 1) % 100 == 0:
                    print(f"Progress: {i+1}/{total} - Resting...")
                    time.sleep(5)

            except Exception as e:
                # [Fact]: data_update_log 테이블에 실패 이력 기록[cite: 3]
                self.conn.execute(
                    "INSERT INTO data_update_log (ticker, date, source, status, error_msg) VALUES (?, ?, ?, ?, ?)",
                    (ticker, end_date, 'pykrx', 'fail', str(e))
                )