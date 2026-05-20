using Quant.Core.Infrastructure;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Quant.UI.Views;

// =============================================================================
// SearchView.xaml.cs  —  종목 검색 뷰
// 연결 진입점: MainWindow.xaml.cs  ShowSearch() → _searchView ??= new SearchView(_db)
// =============================================================================

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
    private void BtnSearch_Click(object sender, RoutedEventArgs e) => ExecuteSearch();

    private void TxtQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ExecuteSearch();
    }

    private void ExecuteSearch()
    {
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
