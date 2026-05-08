"""debug_supply3.py — pykrx investor 함수 반환값 구조 확인"""
import os
from pykrx import stock as krx
import pandas as pd

# pykrx는 import 시점에 KRX_ID/KRX_PW로 자동 로그인
print("=== get_market_trading_volume_by_investor ===")
vol = krx.get_market_trading_volume_by_investor("20250507", "20250508", "005930")
print(f"type: {type(vol)}")
print(f"empty: {vol is None or (hasattr(vol,'empty') and vol.empty)}")
if vol is not None and not vol.empty:
    print(f"shape: {vol.shape}")
    print(f"index type: {type(vol.index)}")
    print(f"columns: {vol.columns.tolist()}")
    print(vol.head())
else:
    print("빈 결과")

print("\n=== get_market_trading_value_by_investor ===")
val = krx.get_market_trading_value_by_investor("20250507", "20250508", "005930")
print(f"type: {type(val)}")
print(f"empty: {val is None or (hasattr(val,'empty') and val.empty)}")
if val is not None and not val.empty:
    print(f"shape: {val.shape}")
    print(f"index type: {type(val.index)}")
    print(f"columns: {val.columns.tolist()}")
    print(val.head())
else:
    print("빈 결과")
