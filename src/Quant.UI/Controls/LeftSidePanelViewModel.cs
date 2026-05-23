using Chaen;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Quant.Core.Infrastructure;
using Quant.Core.Services;

using System.Collections.ObjectModel;
using System.Data;

namespace Quant.UI.Controls;

// ──────────────────────────────────────────────────────────────
//  Global Indicator
// ──────────────────────────────────────────────────────────────
public class GlobalIndicatorItem : ObservableObject
{
    public string Symbol { get; init; } = "";
    public string Label  { get; init; } = "";

    private double _value;
    private double _change;

    public double Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                OnPropertyChanged(nameof(IsPositive));
                OnPropertyChanged(nameof(ChangeSign));
                OnPropertyChanged(nameof(ChangeAbs));
            }
        }
    }

    public double Change
    {
        get => _change;
        set
        {
            if (SetProperty(ref _change, value))
            {
                OnPropertyChanged(nameof(IsPositive));
                OnPropertyChanged(nameof(ChangeSign));
                OnPropertyChanged(nameof(ChangeAbs));
            }
        }
    }

    public bool   IsPositive => Change >= 0;
    public string ChangeSign => Change >= 0 ? "▲" : "▼";
    public string ChangeAbs  => $"{Math.Abs(Change):F2}%";
}

// ──────────────────────────────────────────────────────────────
//  Group row
// ──────────────────────────────────────────────────────────────
public class GroupRow
{
    public int    GroupId    { get; set; }
    public string Kind       { get; set; } = "";
    public string Name       { get; set; } = "";
    public int    StockCount { get; set; }
    public int    Rating     { get; set; }
    public double Ret3m      { get; set; }
}

// ──────────────────────────────────────────────────────────────
//  Screener row  (하드코딩 전용 — 사용자 입력 절대 금지)
// ──────────────────────────────────────────────────────────────
public class ScreenerRow
{
    public int    Id          { get; set; }
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>
    /// ⚠️ SECURITY: 하드코딩된 리터럴만 허용.
    /// 사용자 입력 문자열을 여기에 할당하는 것은 SQL 인젝션 경로이므로 금지.
    /// 편집 view 구현 시 반드시 파라미터 바인딩 또는 AST 빌더로 교체할 것.
    /// </summary>
    public string SqlWhere          { get; set; } = "";

    /// <summary>
    /// true → fundamentals 테이블 INNER JOIN (PBR/PER/ROE 필터 사용 스크리너)
    /// false → LEFT JOIN (ETF 등 재무데이터 없는 종목 포함)
    /// </summary>
    public bool   NeedsFundamentals { get; set; } = true;
}

// ──────────────────────────────────────────────────────────────
//  Stock row
// ──────────────────────────────────────────────────────────────
public class StockRow
{
    public string Ticker { get; set; } = "";
    public string Name   { get; set; } = "";
    public double? Ret_3m { get; set; }
    public string Market { get; set; } = "";
    public double? RS     { get; set; }
    public int    Rating { get; set; }
}

// ──────────────────────────────────────────────────────────────
//  ViewModel
// ──────────────────────────────────────────────────────────────
public partial class LeftSidePanelViewModel : ObservableObject
{
    private readonly DbManager _db = DbManager.Instance;

    // ── 글로벌 인디케이터 ─────────────────────────────────────
    public ObservableCollection<GlobalIndicatorItem> Indicators { get; } = [];

    // ── 옵션 ─────────────────────────────────────────────────
    [ObservableProperty] private bool _showSector     = true;
    [ObservableProperty] private bool _showTheme      = true;

    // ── 탭: GROUPS / SCREENERS ────────────────────────────────
    /// <summary>true = GROUPS 탭, false = SCREENERS 탭</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScreenerTabActive))]
    private bool _isGroupTabActive = true;

    public bool IsScreenerTabActive => !IsGroupTabActive;

    // ── Groups ────────────────────────────────────────────────
    private readonly List<GroupRow>  _allGroups = [];
    public ObservableCollection<GroupRow> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGroup))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private GroupRow? _selectedGroup;

    public bool HasSelectedGroup => SelectedGroup is not null;

    /// <summary>Groups 또는 Screeners 중 하나라도 선택되면 true → StockGrid Opacity 제어용</summary>
    public bool HasSelection => SelectedGroup is not null || SelectedScreener is not null;

    // ── Screeners (하드코딩 전용) ─────────────────────────────
    public ObservableCollection<ScreenerRow> Screeners { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedScreener))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ScreenerRow? _selectedScreener;

    public bool HasSelectedScreener => SelectedScreener is not null;

    // ── Stocks ────────────────────────────────────────────────
    private readonly List<StockRow>  _allStocks = [];
    public ObservableCollection<StockRow> Stocks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedStock))]
    private StockRow? _selectedStock;

    public bool HasSelectedStock => SelectedStock is not null;

    // ── Stock 검색 (메모리 필터) ──────────────────────────────
    [ObservableProperty] private string _stockSearchText = "";

    // ── Stock 섹션 헤더 레이블 ────────────────────────────────
    [ObservableProperty] private string _stockSectionLabel = "";

    // ── 상태 ─────────────────────────────────────────────────
    [ObservableProperty] private string _groupListInfo = "Cnt";
    [ObservableProperty] private string _stockListInfo = "Cnt";
    [ObservableProperty] private string _selectedGroupInfo = "group";
    [ObservableProperty] private string _statusText = "준비";
    [ObservableProperty] private bool   _isBusy     = false;

    // ── 이벤트 ───────────────────────────────────────────────
    public event Action<string, string>? StockSelected;
    public event Action<int, string>?    GroupSelected;
    public event Action<string, string>? IndicatorSelected;

    // ═════════════════════════════════════════════════════════
    public LeftSidePanelViewModel()
    {
        InitIndicators();
        InitScreeners();
    }

    // ──────────────────────────────────────────────────────────
    //  Indicators
    // ──────────────────────────────────────────────────────────
    private void InitIndicators()
    {
        foreach (var def in IndicatorDownloadService.IndicatorDefs)
            Indicators.Add(new GlobalIndicatorItem { Symbol = def.DbTicker, Label = def.Label });
        RefreshIndicatorValues();
    }

    private void RefreshIndicatorValues()
    {
        try
        {
            var svc    = new IndicatorDownloadService(_db);
            var values = svc.LoadLatestValues();
            foreach (var (dbTicker, value, changePct) in values)
                UpdateIndicator(dbTicker, value, changePct);
        }
        catch { }
    }

    public void UpdateIndicator(string symbol, double value, double changePct)
    {
        var item = Indicators.FirstOrDefault(i => i.Symbol == symbol);
        if (item is null) return;
        item.Value  = value;
        item.Change = changePct;
    }

    public void SelectIndicator(GlobalIndicatorItem item)
        => IndicatorSelected?.Invoke(item.Symbol, item.Label);

    [RelayCommand]
    public async Task DownloadIndicatorsAsync()
    {
        if (IsBusy) return;
        IsBusy     = true;
        StatusText = "인디케이터 다운로드 중…";
        try
        {
            var svc      = new IndicatorDownloadService(_db);
            var progress = new Progress<(string symbol, string msg)>(p =>
                System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => StatusText = $"{p.symbol}: {p.msg}"));

            await Task.Run(async () => await svc.DownloadAllAsync(progress));

            System.Windows.Application.Current.Dispatcher.Invoke(RefreshIndicatorValues);
            StatusText = "인디케이터 업데이트 완료";
        }
        catch (Exception ex) { StatusText = $"오류: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ──────────────────────────────────────────────────────────
    //  탭 전환
    // ──────────────────────────────────────────────────────────
    partial void OnIsGroupTabActiveChanged(bool value)
    {
        ClearStocks();
        if (value)
        {
            // GROUPS 탭 활성화 시 선택 초기화
            SelectedScreener = null;
        }
        else
        {
            // SCREENERS 탭 활성화 시 선택 초기화
            SelectedGroup = null;
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Screeners 초기화 (하드코딩)
    // ──────────────────────────────────────────────────────────
    private void InitScreeners()
    {
        // ⚠️ SqlWhere는 하드코딩 리터럴만. 사용자 입력 할당 금지.
        Screeners.Add(new ScreenerRow
        {
            Id = 1,
            Name = "ETF",
            Description = "ETF",
            SqlWhere = "s.security_type = 'ETF'",
            NeedsFundamentals = false
        });
        Screeners.Add(new ScreenerRow
        {
            Id = 2, Name = "KOSPI · PBR < 1.5",
            Description = "코스피 저PBR",
            SqlWhere = "s.market = 'KP' AND f.pbr < 1.5 AND f.pbr > 0"
        });
        Screeners.Add(new ScreenerRow
        {
            Id = 3, Name = "ROE > 15%",
            Description = "고ROE 전체",
            SqlWhere = "f.roe > 15"
        });
        Screeners.Add(new ScreenerRow
        {
            Id = 4, Name = "KOSDAQ · PER < 10",
            Description = "코스닥 저PER",
            SqlWhere = "s.market = 'KQ' AND f.per > 0 AND f.per < 10"
        });
        Screeners.Add(new ScreenerRow
        {
            Id = 5,
            Name = "KOSPI, KOSDAQ",
            Description = "all korean",
            SqlWhere = "s.market = 'KP' OR s.market = 'KQ'",
            NeedsFundamentals = false
        });
    }

    partial void OnSelectedScreenerChanged(ScreenerRow? value)
    {
        ClearStocks();
        if (value is null) return;
        StockSectionLabel = value.Name;
        LoadStocksByScreener(value.SqlWhere);
    }

    /// <summary>
    /// ⚠️ whereClause는 InitScreeners()의 하드코딩 값만 수신해야 함.
    /// 사용자 입력이 경로에 들어오면 SQL 인젝션 위험 — 편집 view 구현 시 교체 필수.
    /// </summary>
    private void LoadStocksByScreener(string whereClause)
    {
        try
        {
            var excludeFilter = _db.BuildStockExcludeFilter("s", "c");
            var needsF        = SelectedScreener?.NeedsFundamentals ?? true;
            var fJoin         = needsF
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
                ORDER BY s.ticker
                """;

            var dt = _db.Query(sql);

            _allStocks.Clear();
            foreach (DataRow row in dt.Rows)
            {
                _allStocks.Add(new StockRow
                {
                    Ticker = ChaenHelper.SafeStr(row, "ticker"),
                    Name = ChaenHelper.SafeStr(row, "name"),
                    Market = ChaenHelper.SafeStr(row, "market"),
                    Ret_3m = ChaenHelper.SafeDouble(row, "ret_3m"),
                    RS     = ChaenHelper.SafeDouble(row, "rs"),
                    Rating = ChaenHelper.SafeInt(row, "rating"),
                });
            }
            ApplyStockFilter();
            StatusText = $"{SelectedScreener?.Name}  {_allStocks.Count}종목";
        }
        catch (Exception ex) { StatusText = $"오류: {ex.Message}"; }
    }

    // ──────────────────────────────────────────────────────────
    //  Groups 로드
    // ──────────────────────────────────────────────────────────
    //public string KindCode => Kind switch { ... };

    [RelayCommand]
    public void LoadGroups()
    {
        try
        {
            IsBusy = true;
            _allGroups.Clear();
            Groups.Clear();
            SelectedGroup = null;
            ClearStocks();

            var dt = _db.Query_GroupList(ShowSector, ShowTheme);

            //var kindFilter = BuildKindFilter();
            //var sql        = DbManager.GroupListSql(kindFilter);

            //var dt = _db.Query(sql);
            foreach (DataRow row in dt.Rows)
            {
                _allGroups.Add(new GroupRow
                {
                    GroupId = ChaenHelper.SafeInt(row, "group_id"),
                    //Kind       = ChaenHelper.SafeStr(row, "kind"),
                    Name = ChaenHelper.SafeStr(row, "name"),
                    Rating = ChaenHelper.SafeInt(row, "rating"),
                    Ret3m = ChaenHelper.SafeDouble(row, "ret_3m"),
                    StockCount = ChaenHelper.SafeInt(row, "stock_count"),
                    Kind = Helpers.GroupKind2Emoji(ChaenHelper.SafeStr(row, "kind"))
                });
            }
            foreach (var g in _allGroups) Groups.Add(g);
            GroupListInfo = $"{Groups.Count}";
            StatusText = $"Groups count: {Groups.Count}";
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    //private string BuildKindFilter()
    //{
    //    string kindFilter = "";
    //    if (!ShowSector)
    //        kindFilter += "AND g.kind != 'sector' ";
    //    if (!ShowTheme)
    //        kindFilter += "AND g.kind != 'theme' ";
    //    return kindFilter.Length > 0 ? kindFilter : "AND 1=1";
    //}

    // ──────────────────────────────────────────────────────────
    //  Group 선택 → Stocks 로드
    // ──────────────────────────────────────────────────────────
    partial void OnSelectedGroupChanged(GroupRow? value)
    {
        ClearStocks();
        if (value is null) return;
        SelectedGroupInfo = value.Name;
        GroupSelected?.Invoke(value.GroupId, value.Name);
        LoadStocksForGroup(value.GroupId);
    }

    private void LoadStocksForGroup(int groupId)
    {
        try
        {
            //var excludeFilter = _db.BuildStockExcludeFilter("s", "c");
            //var sql = $"""
            //    SELECT s.ticker, s.name, s.market, s.rating
            //    FROM stocks s
            //    JOIN stock_group_map sgm ON s.ticker = sgm.ticker
            //    LEFT JOIN stock_cache c  ON c.ticker  = s.ticker
            //    WHERE sgm.group_id = {groupId} {excludeFilter}
            //    ORDER BY s.ticker
            //    """;

            //var dt = _db.Query(sql);

            var dt = _db.Query_StockList(groupId);
            _allStocks.Clear();
            foreach (DataRow row in dt.Rows)
            {
                _allStocks.Add(new StockRow
                {
                    Ticker = ChaenHelper.SafeStr(row, "ticker"),
                    Name   = ChaenHelper.SafeStr(row, "name"),
                    Market = ChaenHelper.SafeStr(row, "market"),
                    Ret_3m = ChaenHelper.SafeDouble(row, "ret_3m"),
                    RS     = ChaenHelper.SafeDouble(row, "rs"),
                    Rating = ChaenHelper.SafeInt(row, "rating"),
                });
            }
            ApplyStockFilter();
            StockListInfo = $"{_allStocks.Count}";
            //StatusText = $"{SelectedGroup?.Name}  {_allStocks.Count}종목";
        }
        catch (Exception ex) { StatusText = $"오류: {ex.Message}"; }
    }

    // ──────────────────────────────────────────────────────────
    //  Stock 검색 — 메모리 필터
    // ──────────────────────────────────────────────────────────
    partial void OnStockSearchTextChanged(string value) => ApplyStockFilter();

    private void ApplyStockFilter()
    {
        var keyword = StockSearchText.Trim();
        Stocks.Clear();
        var filtered = string.IsNullOrEmpty(keyword)
            ? _allStocks
            : _allStocks.Where(s =>
                  s.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                  s.Ticker.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        foreach (var s in filtered) Stocks.Add(s);
    }

    private void ClearStocks()
    {
        _allStocks.Clear();
        Stocks.Clear();
        SelectedStock     = null;
        StockSearchText   = "";
        StockSectionLabel = "";
    }

    partial void OnSelectedStockChanged(StockRow? value)
    {
        if (value is not null) StockSelected?.Invoke(value.Ticker, value.Name);
    }

    partial void OnShowSectorChanged(bool value)     => LoadGroups();
    partial void OnShowThemeChanged(bool value)      => LoadGroups();

}
