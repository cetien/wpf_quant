using Quant.Core.Infrastructure;
using Quant.UI.Controls;

using System.Data;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Quant.UI.Views;

// =============================================================================
// SearchView.xaml.cs  —  종목 검색 뷰
// 연결 진입점: MainWindow.xaml.cs  ShowSearch() → _searchView ??= new SearchView(_db)
// =============================================================================

//TODO: GridFilter에서 검색 조건 조합하여 SQL WHERE 절 생성하는 방식으로 변경.
//  아래 조건들을 조합하여 검색할 수 있도록 checkbox 사용.
//  아래 목록은 예시이므로, 현재 구현 가능한 것만 적용할 것
//  작업 후, 적용된 조건들은 주석에서 삭제
//1. 정배열 - close >ma20 > ma60 > ma120
//2. 거래량 급증(ratio) - 오늘 거래량 > 20일 평균 거래량 * 2
//3. 신고가 근처 - 현재가 >= 120일 신고가 * 0.98
//4. RS 강함 - RS >= 85
//5. 눌림목 - 정배열 상태에서 현재가가 20일선 근처 (±3% 이내)
//6. 고점 대비 조정 - 최근 60일 신고가 대비 10~20% 하락 //   drawdown = (close - high60) / high60  → -20% ~ -10%
//7. 저점 대비 반등 - 최근 60일 저가 대비 10~20% 상승 //   rebound = (close - low60) / low60 → +10% ~ +20%
//8. RS 상위 - RS >= 90
//9. 거래량 증가 - 오늘 거래량 > 20일 평균 거래량 * 1.5
//10. 신고가 돌파 - 현재가 > 120일 신고가
//11. 신고가 돌파 직전 - 현재가 > 120일 신고가 * 0.98 AND 현재가 < 120일 신고가
//12. 20일선 돌파 - 현재가 > 20일선 AND 어제는 20일선 아래
//13. 20일선 돌파 직전 - 현재가 > 20일선 * 0.98 AND 현재가 < 20일선
//14. 20일선 지지 - 현재가 > 20일선 AND 어제도 20일선 위
//15. 20일선 지지 직전 - 현재가 > 20일선 * 0.98 AND 현재가 < 20일선 AND 어제도 20일선 위
//16. 20일선 저항 - 현재가 < 20일선 AND 어제도 20일선 아래
//17. 20일선 저항 직전 - 현재가 < 20일선 * 1.02 AND 현재가 > 20일선 AND 어제도 20일선 아래
//18. 60일선 돌파 - 현재가 > 60일선 AND 어제는 60일선 아래
//19. 60일선 돌파 직전 - 현재가 > 60일선 * 0.98 AND 현재가 < 60일선
//20. 60일선 지지 - 현재가 > 60일선 AND 어제도 60일선 위
//21. 60일선 지지 직전 - 현재가 > 60일선 * 0.98 AND 현재가 < 60일선 AND 어제도 60일선 위
//22. 60일선 저항 - 현재가 < 60일선 AND 어제도 60일선 아래
//23. 60일선 저항 직전 - 현재가 < 60일선 * 1.02 AND 현재가 > 60일선 AND 어제도 60일선 아래
//24. 120일선 돌파 - 현재가 > 120일선 AND 어제는 120일선 아래
//25. 120일선 돌파 직전 - 현재가 > 120일선 * 0.98 AND 현재가 < 120일선
//26. 120일선 지지 - 현재가 > 120일선 AND 어제도 120일선 위
//27. 120일선 지지 직전 - 현재가 > 120일선 * 0.98 AND 현재가 < 120일선 AND 어제도 120일선 위
//28. 120일선 저항 - 현재가 < 120일선 AND 어제도 120일선 아래
//29. 120일선 저항 직전 - 현재가 < 120일선 * 1.02 AND 현재가 > 120일선 AND 어제도 120일선 아래
//30. 20일선과 60일선 골든크로스 - 오늘은 20일선 > 60일선, 어제는 20일선 < 60일선
//31. 20일선과 60일선 데드크로스 - 오늘은 20일선 < 60일선, 어제는 20일선 > 60일선
//32. 20일선과 120일선 골든크로스 - 오늘은 20일선 > 120일선, 어제는 20일선 < 120일선
//33. 20일선과 120일선 데드크로스 - 오늘은 20일선 < 120일선, 어제는 20일선 > 120일선
//33. 60일선과 120일선 골든크로스 - 오늘은 60일선 > 120일선, 어제는 60일선 < 120일선
//34. 60일선과 120일선 데드크로스 - 오늘은 60일선 < 120일선, 어제는 60일선 > 120일선
//35. 20일선과 60일선 골든크로스 직전 - 오늘은 20일선 > 60일선 * 0.98 AND 오늘은 20일선 < 60일선, 어제는 20일선 < 60일선
//36. 20일선과 60일선 데드크로스 직전 - 오늘은 20일선 < 60일선 * 1.02 AND 오늘은 20일선 > 60일선, 어제는 20일선 > 60일선
//37. 20일선과 120일선 골든크로스 직전 - 오늘은 20일선 > 120일선 * 0.98 AND 오늘은 20일선 < 120일선, 어제는 20일선 < 120일선
//38. 20일선과 120일선 데드크로스 직전 - 오늘은 20일선 < 120일선 * 1.02 AND 오늘은 20일선 > 120일선, 어제는 20일선 > 120일선
//39. 60일선과 120일선 골든크로스 직전 - 오늘은 60일선 > 120일선 * 0.98 AND 오늘은 60일선 < 120일선, 어제는 60일선 < 120일선
//40. 60일선과 120일선 데드크로스 직전 - 오늘은 60일선 < 120일선 * 1.02 AND 오늘은 60일선 > 120일선, 어제는 60일선 > 120일선
//41. 20일선과 60일선 골든크로스 + 거래량 급증 - 오늘은 20일선 > 60일선, 어제는 20일선 < 60일선, 오늘 거래량 > 20일 평균 * 2
//42. 20일선과 120일선 골든크로스 + 거래량 급증 - 오늘은 20일선 > 120일선, 어제는 20일선 < 120일선, 오늘 거래량 > 20일 평균 * 2
//43. 60일선과 120일선 골든크로스 + 거래량 급증 - 오늘은 60일선 > 120일선, 어제는 60일선 < 120일선, 오늘 거래량 > 20일 평균 * 2
//44. RS 강함 + 정배열 - RS >= 85 AND close > ma20 AND ma20 > ma60 AND ma60 > ma120
//45. RS 상위 + 정배열 - RS >= 90 AND close > ma20 AND ma20 > ma60 AND ma60 > ma120 AND volume > vol_avg20* 1.5
//46. 신고가 근처 + 거래량 증가 - close >= high_120* 0.98 AND volume > vol_avg20* 2
//47. 눌림목 검색 - 정배열 상태, MA20 근처까지 조정 - close > ma60 AND ABS(close-ma20)/ma20< 0.03
//48. 고점 대비 조정 - 최근 60일 신고가 대비 10~20% 하락 - close < high_60 AND (high_60 - close) / high_60 BETWEEN 0.1 AND 0.2
//49. 저점 대비 반등 - 최근 60일 저가 대비 10~20% 상승 - close > low_60 AND (close - low_60) / low_60 BETWEEN 0.1 AND 0.2
//50. 20일선과 60일선 골든크로스 + 거래량 급증 - 오늘은 20일선 > 60일선, 어제는 20일선 < 60일선, 오늘 거래량 > 20일 평균 * 2
//51. 20일선과 120일선 골든크로스 + 거래량 급증 - 오늘은 20일선 > 120일선, 어제는 20일선 < 120일선, 오늘 거래량 > 20일 평균 * 2
//52. 60일선과 120일선 골든크로스 + 거래량 급증 - 오늘은 60일선 > 120일선, 어제는 60일선 < 120일선, 오늘 거래량 > 20일 평균 * 2
//53. 20일선과 60일선 골든크로스 직전 + 거래량 급증 - 오늘은 20일선 > 60일선 * 0.98 AND 오늘은 20일선 < 60일선, 어제는 20일선 < 60일선, 오늘 거래량 > 20일 평균 * 2
//54. 20일선과 120일선 골든크로스 직전 + 거래량 급증 - 오늘은 20일선 > 120일선 * 0.98 AND 오늘은 20일선 < 120일선, 어제는 20일선 < 120일선, 오늘 거래량 > 20일 평균 * 2
//55. 60일선과 120일선 골든크로스 직전 + 거래량 급증 - 오늘은 60일선 > 120일선 * 0.98 AND 오늘은 60일선 < 120일선, 어제는 60일선 < 120일선, 오늘 거래량 > 20일 평균 * 2
// 52. 20일선과 60일선 데드크로스 직전 + 거래량 급증 - 오늘은 20일선 < 60일선 * 1.02 AND 오늘은 20일선 > 60일선, 어제는 20일선 > 60일선, 오늘 거래량 > 20일 평균 * 2
// 53. 20일선과 120일선 데드크로스 직전 + 거래량 급증 - 오늘은 20일선 < 120일선 * 1.02 AND 오늘은 20일선 > 120일선, 어제는 20일선 > 120일선, 오늘 거래량 > 20일 평균 * 2
// 54. 60일선과 120일선 데드크로스 직전 + 거래량 급증 - 오늘은 60일선 < 120일선 * 1.02 AND 오늘은 60일선 > 120일선, 어제는 60일선 > 120일선, 오늘 거래량 > 20일 평균 * 2
// 53. 20일선과 60일선 데드크로스 직전 + 거래량 급증 - 오늘은 20일선 < 60일선 * 1.02 AND 오늘은 20일선 > 60일선, 어제는 20일선 > 60일선, 오늘 거래량 > 20일 평균 * 2
// 54. 20일선과 120일선 데드크로스 직전 + 거래량 급증 - 오늘은 20일선 < 120일선 * 1.02 AND 오늘은 20일선 > 120일선, 어제는 20일선 > 120일선, 오늘 거래량 > 20일 평균 * 2
// 55. 60일선과 120일선 데드크로스 직전 + 거래량 급증 - 오늘은 60일선 < 120일선 * 1.02 AND 오늘은 60일선 > 120일선, 어제는 60일선 > 120일선, 오늘 거래량 > 20일 평균 * 2
// 54. 20일선과 60일선 데드크로스 직전 + 거래량 급증 - 오늘은 20일선 < 60일선 * 1.02 AND 오늘은 20일선 > 60일선, 어제는 20일선 > 60일선, 오늘 거래량 > 20일 평균 * 2
// 55. 20일선과 120일선 데드크로스 직전 + 거래량 급증 - 오늘은 20일선 < 120일선 * 1.02 AND 오늘은 20일선 > 120일선, 어제는 20일선 > 120일선, 오늘 거래량 > 20일 평균 * 2
// 56. 60일선과 120일선 데드크로스 직전 + 거래량 급증 - 오늘은 60일선 < 120일선 * 1.02 AND 오늘은 60일선 > 120일선, 어제는 60일선 > 120일선, 오늘 거래량 > 20일 평균 * 2
// 57. 20일선과 60일선 골든크로스 직전 + 거래량 급증 - 오늘은 20일선 > 60일선 * 0.98 AND 오늘은 20일선 < 60일선, 어제는 20일선 < 60일선, 오늘 거래량 > 20일 평균 * 2
// 58. 20일선과 120일선 골든크로스 직전 + 거래량 급증 - 오늘은 20일선 > 120일선 * 0.98 AND 오늘은 20일선 < 120일선, 어제는 20일선 < 120일선, 오늘 거래량 > 20일 평균 * 2
// 59. 60일선과 120일선 골든크로스 직전 + 거래량 급증 - 오늘은 60일선 > 120일선 * 0.98 AND 오늘은 60일선 < 120일선, 어제는 60일선 < 120일선, 오늘 거래량 > 20일 평균 * 2
// 52. 20일선과 60일선 데드크로스 직전 + 거래량 급증 - 오늘은 20일선 < 60일선 * 1.02 AND 오늘은 20일선 > 60일선, 어제는 20일선 > 60일선, 오늘 거래량 > 20일 평균 * 2
// 53. 20일선과 120일선 데드크로스 직전 + 거래량 급증 - 오늘은 20일선 < 120일선 * 1.02 AND 오늘은 20일선 > 120일선, 어제는 20일선 > 120일선, 오늘 거래량 > 20일 평균 * 2
// 54. 60일선과 120일선 데드크로스 직전 + 거래량 급증 - 오늘은 60일선 < 120일선 * 1.02 AND 오늘은 60일선 > 120일선, 어제는 60일선 > 120일선, 오늘 거래량 > 20일 평균 * 2
//

//  필터 조합 - 위 조건들을 AND로 조합하여 검색 가능하도록 구현
//  예시: 정배열 + 거래량 급증 + 신고가 근처
//  예시: RS 강함 & 정배열 & 20일선 근처   // WHERE rs >= 85 AND close > ma60 AND ABS(close-ma20)/ma20 < 0.03
//  예시: RS 상위 & 정배열 & 거래량 증가   // WHERE rs >= 90 AND close > ma20 AND ma20 > ma60 AND ma60 > ma120 AND volume > vol_avg20* 1.5
//  예시: 신고가 근처 && 거래량 증가    // WHERE close >= high_120* 0.98 AND volume > vol_avg20* 2
//  예시: 눌림목 검색: 정배열 상태, MA20 근처까지 조정    // WHERE close > ma60 AND ABS(close-ma20)/ma20< 0.03









// ---------------------------------------------------------------------------
// TODO [SCENARIO 1] 텍스트 검색 (기본)
//   트리거  : TxtQuery에 입력 후 Enter 또는 BtnSearch 클릭
//   입력    : 종목명(부분일치) 또는 Ticker(접두사/완전일치)
//   쿼리 대상: stock_cache 테이블 (Name ILIKE '%{q}%' OR Ticker ILIKE '{q}%')
//   출력    : GridResults에 StockCache 리스트 바인딩
//   엣지케이스:
//     - 빈 문자열 → 전체 목록 반환 (상위 500건 LIMIT)
//     - 2글자 미만 → 상태바 경고 표시 후 쿼리 생략 (성능 보호)
//     - 특수문자 입력 → SQL Injection 방지: parameterized query 필수
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// TODO [SCENARIO 2] 필터 조합 검색
//   트리거  : CmbMarket / CmbType / CmbRating SelectionChanged
//   동작    : 텍스트 쿼리와 AND 조합으로 재검색 (자동 실행)
//   필터 항목:
//     - Market   : KP | KQ | NYSE | 전체
//     - Type     : stock | ETF | index | 전체
//     - Rating≥  : 3 | 4 | 5 | 전체
//   구현 방식: BuildWhereClause() 헬퍼가 활성 필터만 AND로 조합
//   주의    : 필터 변경 시 결과가 0건이면 "조건에 맞는 종목 없음" 상태바 표시
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// TODO [SCENARIO 3] 행 선택 → 차트 연동
//   트리거  : GridResults_SelectionChanged (단일 클릭)
//   동작    : 선택된 StockCache의 Ticker/Name을 StatusBar에 미리보기 표시
//   이벤트  : StockSelected?.Invoke(ticker, name) 를 MainWindow가 구독
//             → MainWindow.SidePanel_StockSelected()와 동일 패턴
//   주의    : 뷰 전환은 하지 않음 — 단순 선택만
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// TODO [SCENARIO 4] 행 더블클릭 → ChartView 전환
//   트리거  : GridResults_MouseDoubleClick
//   동작    : StockSelected 이벤트 발행 후 MainWindow가 ShowChart() 호출
//   구현    : event Action<string, string>? StockSelected  (ticker, name)
//             MainWindow에서 _searchView.StockSelected += SidePanel_StockSelected 구독
//   주의    : 이미 ChartView가 열려 있으면 LoadTicker()만 호출 (뷰 재생성 금지)
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// TODO [SCENARIO 5] 결과 정렬
//   트리거  : DataGrid 컬럼 헤더 클릭 (WPF 기본 정렬 동작)
//   1차 정렬: RS 내림차순 (기본값 — 모멘텀 중심 워크플로 가정)
//   확장    : 컬럼별 오름/내림차순 토글, 정렬 상태 유지
//   주의    : DataView.Sort vs CollectionView.SortDescriptions — 바인딩 방식 확인 후 결정
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// TODO [SCENARIO 6] 검색 기록 / 즐겨찾기 (Phase 2 — 현재 미구현)
//   - 최근 검색어 5개 드롭다운 (메모리 내 Queue<string>)
//   - 별표 버튼으로 Watchlist 즉시 추가 (WatchlistRepository 연동)
//   - AppOptions.LastSearchQuery 저장 → 앱 재시작 시 복원
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// TODO [SCENARIO 7] 성능 — 대량 데이터 대응
//   - stock_cache 행 수 예상: KP(800) + KQ(1600) + NYSE(?) = 수천 건
//   - LIMIT 500 기본 적용, 초과 시 "상위 500건만 표시" 경고
//   - 검색 디바운스: TextChanged 이벤트 250ms 지연 후 실행 (DispatcherTimer)
//   - 비동기 실행: Task.Run() + Dispatcher.Invoke() 패턴 (UI 블로킹 방지)
//     → DbBrowserView.RunQuery() 패턴 참고
// ---------------------------------------------------------------------------

public partial class SearchView : UserControl
{
    // ── 이벤트 ──────────────────────────────────────────────────────────────
    public event Action<string, string>? StockSelected;  // (ticker, name)
    public event Action<string, string>? StatusChanged;  // (message, colorHex)

    // ── 상태 ────────────────────────────────────────────────────────────────
    private readonly DbManager _db;
    private List<StockCache>   _allResults  = [];
    private List<StockCache>   _filtered    = [];

    // TODO [SCENARIO 7] 디바운스용 타이머
    // private DispatcherTimer? _debounceTimer;

    // ════════════════════════════════════════════════════════════════════════
    public SearchView(DbManager db)
    {
        _db = db;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // TODO [SCENARIO 7] 디바운스 타이머 초기화
        // _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        // _debounceTimer.Tick += (_, _) => { _debounceTimer.Stop(); ExecuteSearch(); };
        // TxtQuery.TextChanged += (_, _) => { _debounceTimer.Stop(); _debounceTimer.Start(); };

        TxtQuery.Focus();
        SetStatus("종목명 또는 Ticker를 입력하세요.", "#6C7086");
    }

    // ── 검색 실행 ────────────────────────────────────────────────────────────
    private void LoadStocksByScreener(string whereClause)
    {
        try
        {
            whereClause = "1=1";
            var excludeFilter = _db.BuildStockExcludeFilter("s", "c");
            var needsF = true;// SelectedScreener?.NeedsFundamentals ?? true;
            var fJoin = needsF
                ? "JOIN latest_f f ON s.ticker = f.ticker AND f.rn = 1"
                : "LEFT JOIN latest_f f ON s.ticker = f.ticker AND f.rn = 1";
            var sql = $"""
                WITH latest_f AS (
                    SELECT ticker, pbr, per, roe,
                           ROW_NUMBER() OVER (PARTITION BY ticker ORDER BY report_date DESC) AS rn
                    FROM fundamentals
                )
                SELECT s.ticker, s.name, s.market, s.rating, s.security_type, c.ret_3m, c.rs
                FROM stocks s
                {fJoin}
                LEFT JOIN stock_cache c ON c.ticker  = s.ticker
                WHERE {whereClause} {excludeFilter}
                ORDER BY s.ticker LIMIT 500
                """;

            var dt = _db.Query(sql);

            GridResults.ItemsSource = dt.DefaultView;

            var infoPanel = new InfoPanelBuilder(Panel_Info);
            infoPanel.Text($"검색 결과: {dt.Rows.Count}종목", Brushes.Yellow);


            //_allStocks.Clear();
            foreach (DataRow row in dt.Rows)
            {
                //_allStocks.Add(new StockRow
                //{
                //    Ticker = Helpers.SafeStr(row, "ticker"),
                //    Name = Helpers.SafeStr(row, "name"),
                //    Market = Helpers.SafeStr(row, "market"),
                //    Ret_3m = Helpers.SafeDouble(row, "ret_3m"),
                //    RS = Helpers.SafeDouble(row, "rs"),
                //    Rating = Helpers.SafeInt(row, "rating"),
                //});
            }
            //ApplyStockFilter();
            //StatusText = $"{SelectedScreener?.Name}  {_allStocks.Count}종목";
        }
        catch (Exception ex) { SetStatus($"오류: {ex.Message}", "#F38BA8"); }
    }


    private void BtnSearch_Click(object sender, RoutedEventArgs e) => ExecuteSearch();

    private void TxtQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ExecuteSearch();
    }

    private void ExecuteSearch()
    {
        LoadStocksByScreener("");


        var q = TxtQuery.Text.Trim();

        // TODO [SCENARIO 1] 2글자 미만 가드
        // if (q.Length > 0 && q.Length < 2)
        // {
        //     SetStatus("2글자 이상 입력하세요.", "#F9E2AF");
        //     return;
        // }

        // TODO [SCENARIO 1, 2] stock_cache 쿼리 + 필터 조합
        // var where = BuildWhereClause(q);
        // var sql   = $"SELECT * FROM stock_cache WHERE {where} ORDER BY rs DESC LIMIT 500";
        // _allResults = await Task.Run(() => _db.QueryList<StockCache>(sql));
        // ApplyFilters();

        SetStatus($"검색: '{q}' — 미구현 (TODO SCENARIO 1)", "#F9E2AF");
    }

    // ── 필터 변경 ────────────────────────────────────────────────────────────
    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        // TODO [SCENARIO 2] 필터 변경 시 _allResults에 클라이언트 사이드 필터 적용
        // ApplyFilters();
    }

    private void ApplyFilters()
    {
        // TODO [SCENARIO 2] BuildWhereClause 또는 LINQ 필터
        // var market = (CmbMarket.SelectedItem as ComboBoxItem)?.Content.ToString();
        // var type   = (CmbType.SelectedItem as ComboBoxItem)?.Content.ToString();
        // var rating = int.TryParse((CmbRating.SelectedItem as ComboBoxItem)?.Content.ToString(), out var r) ? r : 0;
        //
        // _filtered = _allResults
        //     .Where(s => market == "전체" || s.Market == market)
        //     .Where(s => type   == "전체" || s.SecurityType == type)
        //     .Where(s => rating == 0 || s.Rating >= rating)
        //     .ToList();
        //
        // GridResults.ItemsSource = _filtered;
        // TxtResultCount.Text = $"{_filtered.Count:N0}건";
    }

    // ── 행 선택 / 더블클릭 ───────────────────────────────────────────────────
    private void GridResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // TODO [SCENARIO 3]
        // if (GridResults.SelectedItem is not StockCache s) return;
        // SetStatus($"선택: {s.Name} ({s.Ticker})", "#CDD6F4");
    }

    private void GridResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // TODO [SCENARIO 4]
        // if (GridResults.SelectedItem is not StockCache s) return;
        // StockSelected?.Invoke(s.Ticker, s.Name);
    }

    // ── 초기화 ───────────────────────────────────────────────────────────────
    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        TxtQuery.Clear();
        GridResults.ItemsSource = null;
        TxtResultCount.Text = "";
        _allResults.Clear();
        _filtered.Clear();
        SetStatus("초기화되었습니다.", "#6C7086");
        TxtQuery.Focus();
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────────────
    private void SetStatus(string msg, string hex)
    {
        TxtStatus.Text     = msg;
        StatusChanged?.Invoke(msg, hex);
    }

    private void FilterActivated_Changed(object sender, RoutedEventArgs e)
    {

    }

    // TODO [SCENARIO 2] WHERE 절 빌더
    // private string BuildWhereClause(string q)
    // {
    //     var parts = new List<string>();
    //     if (!string.IsNullOrEmpty(q))
    //         parts.Add($"(name ILIKE '%{q}%' OR ticker ILIKE '{q}%')");
    //
    //     var market = (CmbMarket.SelectedItem as ComboBoxItem)?.Content.ToString();
    //     if (market != "전체") parts.Add($"market = '{market}'");
    //
    //     var type = (CmbType.SelectedItem as ComboBoxItem)?.Content.ToString();
    //     if (type != "전체") parts.Add($"security_type = '{type}'");
    //
    //     if (int.TryParse((CmbRating.SelectedItem as ComboBoxItem)?.Content.ToString(), out var r))
    //         parts.Add($"rating >= {r}");
    //
    //     return parts.Count > 0 ? string.Join(" AND ", parts) : "1";
    // }
}
