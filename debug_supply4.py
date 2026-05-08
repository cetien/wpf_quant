"""debug_supply4.py — 일별 추이 함수 확인"""
from pykrx import stock as krx

print("=== get_market_trading_volume_by_date (ticker 지정) ===")
try:
    df = krx.get_market_trading_volume_by_date("20250507", "20250508", "005930")
    print(f"shape={df.shape}  columns={df.columns.tolist()}")
    print(f"index sample: {df.index[:3].tolist()}")
    print(df.head())
except Exception as e:
    print(f"ERROR: {e}")

print("\n=== get_market_trading_value_by_date (ticker 지정) ===")
try:
    df2 = krx.get_market_trading_value_by_date("20250507", "20250508", "005930")
    print(f"shape={df2.shape}  columns={df2.columns.tolist()}")
    print(f"index sample: {df2.index[:3].tolist()}")
    print(df2.head())
except Exception as e:
    print(f"ERROR: {e}")
