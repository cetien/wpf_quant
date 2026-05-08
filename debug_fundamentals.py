"""debug_fundamentals.py"""
from pykrx import stock as krx
import pandas as pd

for ticker in ["000020", "005930"]:
    print(f"\n=== {ticker} ===")
    try:
        raw = krx.get_market_fundamental_by_date("20260101", "20260508", ticker)
        print(f"shape={raw.shape}  columns={raw.columns.tolist()}")
        print(f"index name={raw.index.name}  index type={type(raw.index)}")
        print(raw.head(3))
    except Exception as e:
        print(f"ERROR: {e}")
