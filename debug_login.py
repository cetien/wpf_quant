"""
debug_login.py — KRX 로그인 + supply API 직접 테스트
"""
import os
from pykrx.website.comm.auth import build_krx_session, warmup_krx_session, login_krx
import requests
from io import BytesIO
import pandas as pd

KRX_ID = os.getenv("KRX_ID")
KRX_PW = os.getenv("KRX_PW")

print(f"KRX_ID: {KRX_ID!r}")
print(f"KRX_PW: {'*' * len(KRX_PW) if KRX_PW else None!r}")

if not KRX_ID or not KRX_PW:
    print("\n[ERROR] 환경변수 미설정. 아래 명령 실행 후 다시 시도:")
    print('  $env:KRX_ID = "your_id"')
    print('  $env:KRX_PW = "your_pw"')
    exit(1)

print("\n[1] KRX 로그인 시도...")
krxs = build_krx_session(KRX_ID, KRX_PW)

if krxs is None:
    print("[ERROR] 로그인 실패")
    exit(1)

print(f"[2] 세션 유효: {krxs.is_valid()}")
print(f"    쿠키: {list(krxs.cookies.keys())}")

# OTP 테스트
OTP_URL  = "https://data.krx.co.kr/comm/fileDn/GenerateOTP/generate.cmd"
DOWN_URL = "https://data.krx.co.kr/comm/fileDn/download_csv/download.cmd"

params = {
    "locale": "ko_KR",
    "trdDd": "20250507",
    "money": "1",
    "csvxls_isNo": "false",
    "name": "fileDown",
    "url": "dbms/MDC/STAT/standard/MDCSTAT02302",
}

print("\n[3] OTP 요청...")
r1 = krxs.post(OTP_URL, data=params)
print(f"  status={r1.status_code}  otp={r1.text[:80]!r}")
otp = r1.text.strip()

if otp == "LOGOUT" or not otp:
    print("[ERROR] 로그인 후에도 LOGOUT — URL 또는 권한 문제")
    exit(1)

print("\n[4] CSV 다운로드...")
r2 = krxs.post(DOWN_URL, data={"code": otp})
print(f"  status={r2.status_code}  len={len(r2.content)}")

if len(r2.content) > 50:
    df = pd.read_csv(BytesIO(r2.content), encoding="EUC-KR", thousands=",")
    print(f"  성공: shape={df.shape}")
    print(f"  컬럼: {list(df.columns)}")
    print(df.head(3).to_string())
else:
    print(f"  빈 응답: {r2.content}")
