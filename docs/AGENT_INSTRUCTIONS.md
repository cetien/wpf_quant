# Quant 프로젝트 — AI Agent 작업 지침서

> **목적**: 이 문서는 AI agent가 컨텍스트 없이 이 프로젝트에 합류해도
> 즉시 올바른 방향으로 코드를 작성할 수 있도록 설계된 작업 지침이다.
> 모든 결정의 근거가 여기에 기록되어 있다.

---

## 1. 프로젝트 개요

| 항목 | 내용 |
|------|------|
| 목적 | 개인용 한국 주식 Quant 분석 도구 |
| 1차 목표 | 매일 10분 안에 시장 상태를 읽는 도구 |
| 실행 환경 | Windows 11, Local PC 전용, 배포 없음 |
| 언어/프레임워크 | C# 12 / .NET 8 / WPF |
| DB | DuckDB (columnar, 패턴 검색 최적) |
| ORM | Dapper (경량 SQL 직접 작성) |
| 데이터 수집 | Python 워커 유지 (pykrx 기반), C# 이전 없음 |

### 레이어 구조 (변경 금지)
```
데이터 수집   → Python 워커 (pykrx) → DuckDB write
Quant.Core    → Models / Repositories / Services / Infrastructure
Quant.UI      → WPF Views / ViewModels (MVVM) / Controls
```

---

## 2. 솔루션 구조

```
C:\Users\tien7\source\repos\quant\
├── quant.sln
├── README.md
├── docs/
│   └── AGENT_INSTRUCTIONS.md          ← 이 파일
├── database/
│   └── migrations/
│       ├── 001_init_schema.sql        ← 테이블 전체 정의 (확정)
│       └── 002_views.sql              ← View 정의 (확정)
└── src/
    ├── Quant.Core/                    ← net8.0 (UI 없는 순수 로직)
    │   ├── Infrastructure/
    │   │   ├── DbConnectionFactory.cs
    │   │   └── SchemaInitializer.cs
    │   ├── Models/
    │   │   ├── Stock.cs               ← Stock, Group, StockGroupMap
    │   │   ├── MarketData.cs          ← DailyPrice, Supply, Fundamentals
    │   │   └── Watchlist.cs           ← Watchlist, WatchlistItem, PdfReport, StockCache
    │   ├── Repositories/
    │   │   ├── StockRepository.cs
    │   │   └── DailyPriceRepository.cs
    │   └── Services/
    │       ├── CacheUpdateService.cs
    │       └── PdfWatcherService.cs
    └── Quant.UI/                      ← net8.0-windows, WPF
        ├── Views/                     ← .xaml 파일
        ├── ViewModels/                ← *ViewModel.cs
        └── Controls/                  ← 재사용 UserControl
```

---

## 3. NuGet 패키지 (확정)

### Quant.Core
| 패키지 | 용도 |
|--------|------|
| `DuckDB.NET.Data` | DuckDB C# 드라이버 |
| `Dapper` | 경량 ORM |

### Quant.UI
| 패키지 | 용도 |
|--------|------|
| `CommunityToolkit.Mvvm` | MVVM (MS 공식, SourceGenerator) |
| `HandyControl` | WPF UI 컴포넌트 (다크테마, 카드 등) |
| `LiveChartsCore.SkiaSharpView.WPF` | 비교차트 (base 100, slider 연동) |
| `Extended.Wpf.Toolkit` | RangeSlider (기간범위 20일~1년) |

> **절대 추가 금지**: EF Core, Prism, MahApps, SQLite, Quartz.NET (Phase 2 이전)

---

## 4. DB 스키마 — 테이블 목록 및 상태

모든 DDL은 `database/migrations/001_init_schema.sql` 에 확정 작성되어 있음.
**schema 변경 시 반드시 migration 파일도 수정할 것.**

### Phase 1 활성 테이블

| 테이블 | 역할 | 비고 |
|--------|------|------|
| `stocks` | 종목 마스터 | PK: ticker |
| `delisted_stocks` | 상폐 이력 | 기존 DB 이전용. 이후 is_active=FALSE 대체 검토 |
| `groups` | sector + theme 통합 | kind IN ('sector','theme') |
| `stock_group_map` | 종목-그룹 N:M | weight DEFAULT 1.0 (개인용 미사용 예상) |
| `fundamentals` | 재무 데이터 | announce_date 기준 join (Look-ahead bias 방지) |
| `daily_prices` | OHLCV | adj_close 필수. amount는 pykrx만 제공 |
| `supply` | 수급 | 기관/외인 순매수 주+금액 |
| `watchlists` | 관심목록 헤더 | watchlist_id=1 → 최근조회(예약) |
| `watchlist_items` | 관심종목 | trigger_reason: 자동추가 원인 |
| `pdf_reports` | 리포트 파일 메타 | filepath UNIQUE, file_hash UNIQUE |
| `data_update_log` | 수집 로그 | status: 'success'|'fail'|'skip' |
| `trading_calendar` | 영업일 | N일 수익률/RS 계산에 필수 |

### Phase 2~3 (현재 미구현 — 코드 작성 금지)
- `watchlist_alerts`, `strategy_signals`, `positions`

### 확정 Views (`002_views.sql`)
- `v_sectors`, `v_themes`
- `v_active_group_map`
- `v_stock_primary_sector`
- `v_sector_performance` (히트맵용, avg_ret_1m/3m/6m/rs)

---

## 5. 미확정 결정 사항 — 반드시 사람에게 확인 후 진행

아래 3개는 **코드 작성 전 확인 필수**. 독단으로 가정하지 말 것.

| # | 항목 | 현재 상태 | 영향 범위 |
|---|------|-----------|-----------|
| 1 | KOSPI RS 기준 ticker | 미확정 | `CacheUpdateService.cs` TODO 완성, `stock_cache.rs` 계산 |
| 2 | `announce_date` 소스 | 미확정 (DART API vs 수동 입력) | `fundamentals` PK 구조, 데이터 수집 파이프라인 |

---

## 6. 코딩 규칙

### 공통
- nullable enable, implicit usings enable 적용됨
- 모든 public API에 XML doc 주석 (`///`) 작성
- async/await 사용 (DB 호출 포함) — Dapper의 `QueryAsync`, `ExecuteAsync` 사용
- 예외는 catch 후 `data_update_log`에 기록 (source, status='fail', error_msg)

### Quant.Core
- Repository는 단일 테이블 책임. JOIN이 필요하면 View를 쿼리할 것
- Service는 Repository를 조합하는 비즈니스 로직 계층
- `DbConnectionFactory.Create()`로 매번 새 connection 생성 (DuckDB는 파일 lock 주의)
- DuckDB는 `INSERT OR IGNORE` 또는 `ON CONFLICT DO UPDATE` 패턴 사용
- `DateOnly` ↔ DuckDB `DATE` 매핑 사용 (`DateTime` 사용 금지 — 날짜 전용 필드)

### Quant.UI (WPF + MVVM)
- `[ObservableProperty]`, `[RelayCommand]` 어트리뷰트 사용 (CommunityToolkit)
- code-behind는 최소화. 로직은 ViewModel로
- View → ViewModel 의존만 허용. ViewModel → View 역참조 금지
- HandyControl 컴포넌트 우선 사용. WPF 기본 컨트롤은 대안이 없을 때만
- 차트: `LiveChartsCore.SkiaSharpView.WPF` — `CartesianChart` UserControl
- 기간 슬라이더: `Extended.Wpf.Toolkit`의 `RangeSlider` (20일~365일)

### 파일 작명
| 유형 | 패턴 | 예시 |
|------|------|------|
| View | `*View.xaml` + `*View.xaml.cs` | `DashboardView.xaml` |
| ViewModel | `*ViewModel.cs` | `DashboardViewModel.cs` |
| Repository | `*Repository.cs` | `SupplyRepository.cs` |
| Service | `*Service.cs` | `RsCalculationService.cs` |
| Model | 단수형 | `Stock.cs`, `DailyPrice.cs` |

---

## 7. Phase 1 작업 목록 (우선순위 순)

> 체크박스는 완료 시 체크. 각 태스크 완료 후 이 파일 업데이트.

### 7-1. Infrastructure / 기반
- [ ] `SchemaInitializer` — migration 실행 후 seed data 삽입 (watchlist_id=1 최근조회 insert)
- [ ] `DbConnectionFactory` — async 버전 `CreateAsync()` 추가
- [ ] `trading_calendar` 초기 데이터 삽입 스크립트 (KRX 공휴일 기준 2020~2030)

### 7-2. 데이터 수집 연동 (Python 워커 → DuckDB)
- [ ] Python 워커가 write하는 DuckDB path와 C# 앱이 read하는 path 일치 확인
- [ ] `data_update_log` 기록 로직 — 모든 수집 완료/실패 시 insert
- [ ] `StockRepository.Upsert()` — 기존 DB 이전용 bulk upsert 검증

### 7-3. Repository 완성
- [ ] `SupplyRepository` — GetRange, BulkInsert
- [ ] `FundamentalsRepository` — GetLatest(ticker), GetHistory(ticker)
- [ ] `WatchlistRepository` — GetAll, AddItem, RemoveItem, GetItems(watchlistId)
- [ ] `PdfReportRepository` — GetByTicker, GetByDateRange, Insert
- [ ] `GroupRepository` — GetSectors, GetThemes, GetStocksByGroup(groupId)
- [ ] `StockCacheRepository` — GetAll (대시보드용), UpdateBatch

### 7-4. Services 완성
- [ ] `CacheUpdateService` — **KOSPI ticker 확정 후** rs, beta_60d 계산 구현
- [ ] `RsCalculationService` — KOSPI 대비 RS, theme 평균 대비 RS (별도 분리 권장)
- [ ] `PdfWatcherService` — 검증 (실제 folder 경로 사용자 설정에서 읽기)
- [ ] `DataUpdateService` — app 시작 시 check update 트리거

### 7-5. UI — 메인 윈도우
- [ ] `MainWindow.xaml` — 3:7 레이아웃 (leftPanel min 300px + GridSplitter + mainView)
- [ ] `MainWindow.xaml` — Top: 풀다운 메뉴 + Tool Buttons
- [ ] `MainWindow.xaml` — Status bar: DB 상태, 주요지수
- [ ] `MainWindowViewModel.cs`

### 7-6. UI — Left Panel
- [ ] Sector/Theme 목록 (TreeView 또는 ListBox)
- [ ] Ticker 검색 + 목록
- [ ] 선택 시 mainView 탭 연동

### 7-7. UI — 대시보드 탭 (MVP 핵심)
- [ ] 시장 요약 (KOSPI/KOSDAQ 등락률, 거래대금)
- [ ] 섹터/테마 히트맵 (`v_sector_performance` 기반)
- [ ] 매수 후보 TOP 5 (stock_cache.rs 상위)

### 7-8. UI — 비교차트 탭
- [ ] `CompareChartView.xaml` — LiveCharts2 CartesianChart
- [ ] base 100 정규화 (선택 기간 첫날 = 100)
- [ ] RangeSlider 연동 (20일~365일)
- [ ] legend에 기간 수익률 표시 (예: 삼성전자 +25.3%)
- [ ] yaxis right side, crosshair, h/v grid
- [ ] 최대 20개 종목 오버레이

### 7-9. UI — 종목 상세 탭
- [ ] fundamentals 요약 (PER, PBR, ROE, 매출, 영업이익)
- [ ] RS, beta_60d 표시
- [ ] 수급 (기관/외인 순매수 bar chart, 5일/20일 누적)

### 7-10. UI — 리포트 탭
- [ ] `PdfReportListView.xaml` — 종목별 리포트 목록
- [ ] Click → Chrome (또는 시스템 기본 PDF 뷰어) 외부 실행

### 7-11. UI — 관리 탭
- [ ] DB data table 조회 (DataGrid)
- [ ] SQL 직접 실행 (TextBox input → DuckDB execute → DataGrid 표시)

---

## 8. 현재 구현된 코드 상태

### 완료 (수정 가능, 삭제 금지)
| 파일 | 상태 | 비고 |
|------|------|------|
| `001_init_schema.sql` | ✅ 확정 | 테이블 전체 |
| `002_views.sql` | ✅ 확정 | View 전체 |
| `DbConnectionFactory.cs` | ✅ 기본 완성 | async 버전 추가 필요 |
| `SchemaInitializer.cs` | ✅ 기본 완성 | seed 데이터 추가 필요 |
| `Stock.cs` | ✅ 완성 | Stock, Group, StockGroupMap 포함 |
| `MarketData.cs` | ✅ 완성 | DailyPrice, Supply, Fundamentals 포함 |
| `Watchlist.cs` | ✅ 완성 | Watchlist, WatchlistItem, PdfReport, StockCache 포함 |
| `StockRepository.cs` | ✅ 기본 완성 | GetAll, GetByTicker, Upsert |
| `DailyPriceRepository.cs` | ✅ 기본 완성 | GetRange, GetLastDate, BulkInsert |
| `CacheUpdateService.cs` | ⚠️ TODO | rs, beta_60d 미완성 (KOSPI ticker 미확정) |
| `PdfWatcherService.cs` | ✅ 기본 완성 | folder 경로 설정 연동 필요 |

### 미생성 (작업 필요)
- 모든 Views, ViewModels, Controls
- SupplyRepository, FundamentalsRepository, WatchlistRepository, etc.
- RsCalculationService, DataUpdateService
- App.xaml, App.xaml.cs (DI 컨테이너 설정 포함)

---

## 9. DI 컨테이너 설정 가이드 (App.xaml.cs 작성 시 참고)

```csharp
// App.xaml.cs 작성 시 이 패턴 사용
// Microsoft.Extensions.DependencyInjection 사용 (별도 패키지 불필요, .NET 8 내장)

services.AddSingleton<DbConnectionFactory>(sp =>
    new DbConnectionFactory(@"C:\Users\tien7\AppData\Local\quant\quant.db"));

services.AddSingleton<SchemaInitializer>();
services.AddTransient<StockRepository>();
services.AddTransient<DailyPriceRepository>();
// ... 나머지 Repository/Service

services.AddSingleton<CacheUpdateService>();
services.AddSingleton<PdfWatcherService>(sp =>
    new PdfWatcherService(
        sp.GetRequiredService<DbConnectionFactory>(),
        watchFolder: /* 사용자 설정에서 로드 */));
```

> DB 파일 경로: `%LOCALAPPDATA%\quant\quant.db` 사용 권장 (소스 repo 외부)

---

## 10. 절대 하지 말아야 할 것

1. **`database/migrations/` 파일을 앱 코드에서 직접 수정** — migration 파일은 append-only
2. **Phase 2~3 테이블/기능 선구현** — `watchlist_alerts`, `strategy_signals`, `positions`, `Quartz.NET`
3. **EF Core 추가** — 복잡한 집계 쿼리 비효율. Dapper + 직접 SQL 유지
4. **실시간 자동매매 관련 코드** — 명시적 제외 기능
5. **완전 자동 투자판단 로직** — 사람이 최종 결정하는 구조 유지
6. **미확정 사항(§5)을 가정하여 구현** — 반드시 사용자 확인 먼저

---

## 11. 참고 문서

| 문서 | 위치 |
|------|------|
| 기획 원본 | Google Drive: `quant 제작 계획` (문서 ID: `1uleI5rL-T6tpsfpVHsRDIhiRGIXzEmfaCTEUjWMmIUc`) |
| DB Schema | `database/migrations/001_init_schema.sql` |
| Views | `database/migrations/002_views.sql` |
| 프로젝트 개요 | `README.md` |

---

*최종 업데이트: 2026-05-04*
*다음 작업 시작 권장: §7-1 SchemaInitializer seed 데이터 → §7-5 MainWindow 레이아웃*
