"""
debug_supply.py — KRX supply 수집 진단 스크립트
실행: python debug_supply.py
"""
import requests
from io import BytesIO
import pandas as pd

OTP_URL  = "http://data.krx.co.kr/comm/fileDn/GenerateOTP/generate.cmd"
DOWN_URL = "http://data.krx.co.kr/comm/fileDn/download_csv/download.cmd"

HEADERS = {
    "Referer": "http://data.krx.co.kr/contents/MDC/MDI/mdiLoader/index.cmd?menuId=MDC0201020301",
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
}

TEST_DATE = "20250507"  # 수요일 — 영업일

def test_endpoint(label: str, url_key: str, extra_params: dict = {}):
    print(f"\n{'='*60}")
    print(f"[{label}]  url={url_key}")

    params = {
        "locale": "ko_KR",
        "trdDd": TEST_DATE,
        "money": "1",
        "csvxls_isNo": "false",
        "name": "fileDown",
        "url": url_key,
        **extra_params,
    }

    # Step 1: OTP
    r1 = requests.post(OTP_URL, data=params, headers=HEADERS, timeout=15)
    print(f"  OTP status={r1.status_code}  len={len(r1.text)}  otp={r1.text[:80]!r}")
    otp = r1.text.strip()
    if not otp or r1.status_code != 200:
        print("  → OTP 실패, skip")
        return

    # Step 2: CSV download
    r2 = requests.post(DOWN_URL, data={"code": otp}, headers=HEADERS, timeout=30)
    print(f"  CSV status={r2.status_code}  len={len(r2.content)}  type={r2.headers.get('Content-Type','')}")
    print(f"  raw[:200]: {r2.content[:200]}")

    if len(r2.content) > 50:
        try:
            df = pd.read_csv(BytesIO(r2.content), encoding="EUC-KR", thousands=",")
            print(f"  파싱 성공: shape={df.shape}")
            print(f"  컬럼: {list(df.columns)}")
            print(df.head(3).to_string())
        except Exception as e:
            print(f"  파싱 실패: {e}")
            # UTF-8 시도
            try:
                df = pd.read_csv(BytesIO(r2.content), encoding="UTF-8", thousands=",")
                print(f"  UTF-8 파싱 성공: shape={df.shape}  컬럼: {list(df.columns)}")
            except Exception as e2:
                print(f"  UTF-8도 실패: {e2}")


if __name__ == "__main__":
    # 후보 1: 전체시장 투자자별 거래실적 (날짜별)
    test_endpoint("MDCSTAT02302 전체", "dbms/MDC/STAT/standard/MDCSTAT02302")

    # 후보 2: 투자자별 거래실적 개별종목 추이 (isuCd 필요)
    test_endpoint(
        "MDCSTAT02301 개별종목",
        "dbms/MDC/STAT/standard/MDCSTAT02301",
        {"isuCd": "KR7005930003", "isuCd2": "005930"},  # 삼성전자
    )

    # 후보 3: 투자자별 순매수상위 (날짜별 전종목)
    test_endpoint("MDCSTAT02401 순매수상위", "dbms/MDC/STAT/standard/MDCSTAT02401",
                  {"mktId": "STK"})

    # 후보 4: 기관/외국인 전종목 (날짜별, mktId 필요)
    test_endpoint("MDCSTAT02302 STK", "dbms/MDC/STAT/standard/MDCSTAT02302",
                  {"mktId": "STK"})

    print("\n완료")
