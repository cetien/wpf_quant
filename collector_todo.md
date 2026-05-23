
---

# TODO: Daily Price Anomaly Detection & Auto Re-download

## 목적

현재 `collector.py`는 종목별 가격 데이터를 다운로드하여 `daily_prices` 테이블에 저장하고 있다.

일반적인 Incremental Update 방식에서는 액면분할, 액면병합, 감자, 권리락 등의 이벤트 발생 시 과거 데이터와 신규 데이터의 정합성이 깨질 수 있다.

이를 자동으로 탐지하여 해당 종목만 전체 재다운로드(Full Refresh)하도록 개선한다.

--> 가격 변동의 원인은 판별하지 않는다. Full Refresh 대상으로 분류한다.

## 참고
이 파일의 내용은 chatGPT가 제안한 초안이므로, 전체를 완전하게 따를 필요는 없다.
상세를 일부 수정하거나 또는 더 좋은 방법으로 개선해도 된다.


---

# 요구사항

## 1. 이상 종목 탐지

`daily_prices` 테이블을 조회하여 비정상적인 가격 변동이 발생한 종목을 탐지한다.

### 탐지 기준

전일 종가 대비:

```text
ABS(close / prev_close - 1) >= 0.29
```

또는

```text
ABS(open / prev_close - 1) >= 0.29
```

조건을 만족하면 이상 종목으로 판단한다.

### 계산 방식

```sql
LAG(close)
OVER (
    PARTITION BY ticker
    ORDER BY trade_date
)
```

사용.

---

## 2. 검사 범위

전체 이력 검사 불필요.

최근 N일만 검사.

예:

```python
LOOKBACK_DAYS = 30
```

또는

```python
LOOKBACK_DAYS = 90
```

설정 가능하도록 구현.

---

## 3. 이상 종목 목록 생성

예시:

```python
[
    "005930",
    "000660",
    "035420"
]
```

중복 제거 필수.

---

## 4. 재다운로드 수행

탐지된 종목에 대해 기존 `collector.py`를 재사용한다.

CLI 인자를 추가.

예.
```bash
python collector.py --anomaly_refresh --dry-run
python collector.py --anomaly_refresh
```

## 5. Dry Run 모드

실제 다운로드 없이 결과만 출력.

예:

출력:

```text
[ANOMALY]
005930 종목이름
000660 종목이름
035420 종목이름

3 tickers require refresh.
```

---

## 6. 실행 로그

예시:

```text
[SCAN] checking recent 30 days

[ANOMALY]
ticker=005930
name=삼성전자
date005930
date=2026-05-15
prev_close=100000
close=20000
change=-80.00%

[REFRESH]
ticker=005930

[DONE]
```


# SQL 예시 (더 좋은 방법으로 변경 가능)

```sql
WITH changes AS (
    SELECT
        ticker,
        trade_date,
        open,
        close,
        LAG(close) OVER (
            PARTITION BY ticker
            ORDER BY trade_date
        ) AS prev_close
    FROM daily_prices
    WHERE trade_date >= CURRENT_DATE - INTERVAL 90 DAY
)
SELECT DISTINCT ticker
FROM changes
WHERE prev_close IS NOT NULL
  AND (
      ABS(close / prev_close - 1) >= 0.29
      OR
      ABS(open / prev_close - 1) >= 0.29
  );
```

---

# 구현 시 주의사항

전체 종목 Full Refresh 금지.

반드시:

```text
이상 종목만 재다운로드
```

하도록 구현.

