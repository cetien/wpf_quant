
"RS 상위 + 신고가 근처 + 거래량 증가"

A. RS 필터
    RS Rank >= 85
    3M, 6M 수익률 상위 15%

B. 신고가 근접
    DistanceFromHigh >= -10

C. 거래량 증가 : Relative Volume (RVOL)
    오늘 거래량 / 20일 평균 거래량
    RVOL >= 1.5

1. 유동성 필터
    20일 평균 거래대금 >= 50억

    안 그러면:
        호가 비고
        갭 심함
        백테스트 왜곡
    발생.    

2. 변동성 제한

    예: ATR% <= 8
    너무 광기인 종목 제외.

3. 장기 추세 확인

    예: 현재가 > MA50 > MA150 > MA200
    이거 굉장히 강력합니다.
    미국 CANSLIM 계열도 매우 중시.    

"Leader Scan"
- RS Rank >= 90
- DistanceFromHigh >= -8%
- RVOL >= 1.5
- Price > MA50 > MA150 > MA200
- AvgValue20D >= 50억
- ATR% <= 7

    이렇게 하면:

        강한 리더주
        기관 수급 가능성
        유동성 확보
        추세 지속 가능성

    을 동시에 잡습니다.    


# 추천 구조를 분리하는 게 좋습니다

1. Momentum Leaders

    강세 지속형.

    지금 논의한 RS 기반.

2. Early Breakout

    막 돌파 시작.

        거래량 급증
        박스 돌파
        신고가 갱신 직전

    위주.

3. Mean Reversion

    낙폭과대 반등형.

    RS 낮은 종목.

4. Quality Growth

펀더멘털 + 추세.

    ROE
    EPS Growth
    Sales Growth
    RS

혼합.

