# quant — 개인용 Quant 분석 프로그램

WPF/C# + DuckDB + Dapper 기반. 개인 Windows Local PC 전용.

## 목표
매일 10분 안에 시장 상태를 읽는 도구.
향후 투자 시스템으로 확장: 데이터 → 신호 → 규칙 → 포지션

## 프로젝트 구조
```
start at C:\Users\tien7\source\repos\quant

quant/
├── quant.sln
├── database/
│   └── migrations/
│       ├── 001_init_schema.sql    # 테이블 정의
│       └── 002_views.sql          # View 정의
├── docs/
└── src/
    ├── Quant.Core/                # 비즈니스 로직, DB, 모델
    │   ├── Infrastructure/
    │   │   ├── DbConnectionFactory.cs
    │   │   └── SchemaInitializer.cs
    │   ├── Models/
    │   │   ├── Stock.cs           # Stock, Group, StockGroupMap
    │   │   ├── MarketData.cs      # DailyPrice, Supply, Fundamentals
    │   │   └── Watchlist.cs       # Watchlist, WatchlistItem, PdfReport, StockCache
    │   ├── Repositories/
    │   │   ├── StockRepository.cs
    │   │   └── DailyPriceRepository.cs
    │   └── Services/
    │       ├── CacheUpdateService.cs
    │       └── PdfWatcherService.cs
    └── Quant.UI/                  # WPF 프론트엔드
        ├── Views/
        ├── ViewModels/
        └── Controls/
```

## 스택
| 계층 | 라이브러리 |
|------|-----------|
| UI | WPF + HandyControl |
| MVVM | CommunityToolkit.Mvvm |
| 차트 | LiveChartsCore.SkiaSharpView.WPF |
| 슬라이더 | Extended.Wpf.Toolkit |
| ORM | Dapper |
| DB | DuckDB |
| 데이터 수집 | Python 워커 유지 (pykrx) |

## DB 테이블 (Phase 1)
- stocks, groups, stock_group_map
- daily_prices, supply, fundamentals
- watchlists, watchlist_items, pdf_reports
- data_update_log, trading_calendar

## Phase 계획
- **Phase 1 (현재)**: 데이터 수집, 비교차트, RS/베타, 재무, 리포트 목록
- **Phase 2**: 수급 고도화, 상관계수, 리포트 통계, 패턴 검색, 수익률 분석
- **Phase 3**: 알림/자동화

## 미확정 결정 필요 항목
1. `announce_date` 소스 — DART API 연동 또는 수동 입력
2. KOSPI RS 기준 ticker 확정
