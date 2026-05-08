"""
debug_supply2.py — KRX 세션 쿠키 방식 진단
KRX 정보데이터시스템은 로그인 없이도 브라우저 세션(쿠키)이 있어야 OTP 발급됨.
실행: python debug_supply2.py
"""
import requests
from io import BytesIO
import pandas as pd

OTP_URL   = "http://data.krx.co.kr/comm/fileDn/GenerateOTP/generate.cmd"
DOWN_URL  = "http://data.krx.co.kr/comm/fileDn/download_csv/download.cmd"
INDEX_URL = "http://data.krx.co.kr/contents/MDC/MDI/mdiLoader/index.cmd?menuId=MDC0201020301"

HEADERS_BASE = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                  "Chrome/124.0.0.0 Safari/537.36",
    "Accept-Language": "ko-KR,ko;q=0.9",
}

TEST_DATE = "20250507"

def test_with_session():
    sess = requests.Session()
    sess.headers.update(HEADERS_BASE)

    # Step 1: 페이지 먼저 방문 → 세션 쿠키 획득
    print("[1] 인덱스 페이지 방문 (쿠키 획득)")
    r0 = sess.get(INDEX_URL, timeout=15)
    print(f"  status={r0.status_code}  cookies={dict(sess.cookies)}")

    # Step 2: OTP 요청 (Referer = 방문한 페이지)
    params = {
        "locale": "ko_KR",
        "trdDd": TEST_DATE,
        "money": "1",
        "csvxls_isNo": "false",
        "name": "fileDown",
        "url": "dbms/MDC/STAT/standard/MDCSTAT02302",
    }
    sess.headers["Referer"] = INDEX_URL

    print("\n[2] OTP 요청")
    r1 = sess.post(OTP_URL, data=params, timeout=15)
    print(f"  status={r1.status_code}  len={len(r1.text)}  otp={r1.text[:80]!r}")
    otp = r1.text.strip()

    if otp == "LOGOUT" or not otp:
        print("\n  → 여전히 LOGOUT. 로그인 필요 여부 확인 중...")
        # KRX는 회원가입 없이 익명 접근 가능한지 확인
        # 다른 Referer 시도
        for menu in [
            "MDC0201020301",  # 투자자별 거래실적
            "MDC0201020302",  # 개별종목
        ]:
            referer = f"http://data.krx.co.kr/contents/MDC/MDI/mdiLoader/index.cmd?menuId={menu}"
            sess2 = requests.Session()
            sess2.headers.update(HEADERS_BASE)
            sess2.get(referer, timeout=10)
            sess2.headers["Referer"] = referer
            r_test = sess2.post(OTP_URL, data=params, timeout=15)
            print(f"  menu={menu}  otp={r_test.text[:40]!r}")
        return

    # Step 3: CSV 다운로드
    print("\n[3] CSV 다운로드")
    r2 = sess.post(DOWN_URL, data={"code": otp}, timeout=30)
    print(f"  status={r2.status_code}  len={len(r2.content)}")
    if len(r2.content) > 50:
        try:
            df = pd.read_csv(BytesIO(r2.content), encoding="EUC-KR", thousands=",")
            print(f"  성공: shape={df.shape}  컬럼={list(df.columns[:6])}")
            print(df.head(3).to_string())
        except Exception as e:
            print(f"  파싱 실패: {e}  raw: {r2.content[:300]}")
    else:
        print(f"  빈 응답: {r2.content}")


if __name__ == "__main__":
    test_with_session()
