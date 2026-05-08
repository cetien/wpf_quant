"""
debug_login2.py — 올바른 KRX supply endpoint 탐색
"""
import os
from pykrx.website.comm.auth import build_krx_session
from io import BytesIO
import pandas as pd

KRX_ID = os.getenv("KRX_ID")
KRX_PW = os.getenv("KRX_PW")

krxs = build_krx_session(KRX_ID, KRX_PW)
if not krxs:
    print("로그인 실패"); exit(1)

OTP_URL  = "https://data.krx.co.kr/comm/fileDn/GenerateOTP/generate.cmd"
DOWN_URL = "https://data.krx.co.kr/comm/fileDn/download_csv/download.cmd"

def try_endpoint(label, url_key, extra={}):
    params = {
        "locale": "ko_KR",
        "csvxls_isNo": "false",
        "name": "fileDown",
        "url": url_key,
        **extra,
    }
    r1 = krxs.post(OTP_URL, data=params)
    otp = r1.text.strip()
    if otp == "LOGOUT":
        print(f"[{label}] LOGOUT"); return
    r2 = krxs.post(DOWN_URL, data={"code": otp})
    raw = r2.content
    if len(raw) < 10:
        print(f"[{label}] 빈응답"); return
    try:
        df = pd.read_csv(BytesIO(raw), encoding="EUC-KR", thousands=",")
        print(f"[{label}] shape={df.shape}  cols={list(df.columns[:8])}")
        if len(df) > 0:
            print(df.head(2).to_string())
    except Exception as e:
        decoded = raw[:120].decode("euc-kr", errors="replace")
        print(f"[{label}] parse err={e}  raw={decoded!r}")

# 1. 날짜별 전종목 투자자별 (시장전체) — fromdate/todate 방식
try_endpoint("02302 기간", "dbms/MDC/STAT/standard/MDCSTAT02302",
    {"fromdate": "20250507", "todate": "20250507"})

# 2. 개별종목 투자자별 추이 — isuCd 방식
try_endpoint("02301 삼성전자", "dbms/MDC/STAT/standard/MDCSTAT02301",
    {"fromdate": "20250507", "todate": "20250507",
     "isuCd": "KR7005930003", "isuCd2": "005930"})

# 3. 투자자별 순매수 상위 — 전종목
try_endpoint("02401 STK", "dbms/MDC/STAT/standard/MDCSTAT02401",
    {"trdDd": "20250507", "mktId": "STK", "invstTpCd": "4000"})  # 4000=기관합계

try_endpoint("02401 KSQ", "dbms/MDC/STAT/standard/MDCSTAT02401",
    {"trdDd": "20250507", "mktId": "KSQ", "invstTpCd": "4000"})

# 4. 전종목 투자자별 (날짜+시장)
try_endpoint("02302 STK trdDd", "dbms/MDC/STAT/standard/MDCSTAT02302",
    {"trdDd": "20250507", "mktId": "STK"})

# 5. pykrx 내부가 실제로 쓰는 endpoint 확인
try_endpoint("02302 KSQ trdDd", "dbms/MDC/STAT/standard/MDCSTAT02302",
    {"trdDd": "20250507", "mktId": "KSQ"})

# 6. 외국인 보유 추이 (참고)
try_endpoint("03701 외국인보유", "dbms/MDC/STAT/standard/MDCSTAT03701",
    {"trdDd": "20250507", "mktId": "STK"})
