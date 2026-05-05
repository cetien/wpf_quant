using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuckDB.NET.Data;
using System.Collections.ObjectModel;
using System.IO;

namespace Quant.UI.Controls;

// ──────────────────────────────────────────────────────────────
//  Global Indicator (SOX, WTI, KOSPI, S&P500, …)
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
    public string Kind       { get; set; } = "";   // sector | theme
    public string Name       { get; set; } = "";
    public int    StockCount { get; set; }
    public int    Rating     { get; set; }
}

// ──────────────────────────────────────────────────────────────
//  Stock row (within selected group)
// ──────────────────────────────────────────────────────────────
public class StockRow
{
    public string Ticker { get; set; } = "";
    public string Name   { get; set; } = "";
    public string Market { get; set; } = "";
    public int    Rating { get; set; }
}

// ──────────────────────────────────────────────────────────────
//  ViewModel
// ──────────────────────────────────────────────────────────────
public partial class LeftSidePanelViewModel : ObservableObject
{
    private static readonly string DbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "quant", "quant.duckdb");

    // ── 글로벌 인디케이터 ─────────────────────────────────────
    public ObservableCollection<GlobalIndicatorItem> Indicators { get; } = [];

    // ── 옵션 ─────────────────────────────────────────────────
    [ObservableProperty] private bool _showOnlyActive = true;
    [ObservableProperty] private bool _showSector     = true;
    [ObservableProperty] private bool _showTheme      = true;

    // ── Groups ────────────────────────────────────────────────
    public ObservableCollection<GroupRow> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGroup))]
    private GroupRow? _selectedGroup;

    public bool HasSelectedGroup => SelectedGroup is not null;

    // ── Stocks ────────────────────────────────────────────────
    public ObservableCollection<StockRow> Stocks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedStock))]
    private StockRow? _selectedStock;

    public bool HasSelectedStock => SelectedStock is not null;

    // ── 상태 ─────────────────────────────────────────────────
    [ObservableProperty] private string _statusText = "준비";
    [ObservableProperty] private bool   _isBusy     = false;

    // ── 이벤트: 외부 뷰에 종목/그룹 선택 알림 ─────────────────
    public event Action<string>? StockSelected;   // ticker
    public event Action<int>?    GroupSelected;   // group_id

    // ═════════════════════════════════════════════════════════
    public LeftSidePanelViewModel()
    {
        InitIndicators();
    }

    // ──────────────────────────────────────────────────────────
    //  Indicators (더미 초기값 – 추후 실시간 API로 교체)
    // ──────────────────────────────────────────────────────────
    private void InitIndicators()
    {
        Indicators.Add(new GlobalIndicatorItem { Symbol = "SOX",   Label = "SOX",    Value = 5_123.4,  Change =  1.23 });
        Indicators.Add(new GlobalIndicatorItem { Symbol = "WTI",   Label = "WTI",    Value =    82.3,  Change = -0.45 });
        Indicators.Add(new GlobalIndicatorItem { Symbol = "KOSPI", Label = "KOSPI",  Value = 2_634.1,  Change =  0.87 });
        Indicators.Add(new GlobalIndicatorItem { Symbol = "SPX",   Label = "S&P500", Value = 5_408.2,  Change =  0.31 });
        Indicators.Add(new GlobalIndicatorItem { Symbol = "NDX",   Label = "NASDAQ", Value = 18_972.3, Change =  0.56 });
        Indicators.Add(new GlobalIndicatorItem { Symbol = "DXY",   Label = "DXY",    Value =   104.8,  Change = -0.12 });
    }

    /// <summary>외부 피드에서 실시간 인디케이터 값 업데이트</summary>
    public void UpdateIndicator(string symbol, double value, double changePct)
    {
        var item = Indicators.FirstOrDefault(i => i.Symbol == symbol);
        if (item is null) return;
        item.Value  = value;
        item.Change = changePct;
    }

    // ──────────────────────────────────────────────────────────
    //  Groups 로드
    // ──────────────────────────────────────────────────────────
    [RelayCommand]
    public void LoadGroups()
    {
        try
        {
            IsBusy = true;
            Groups.Clear();
            SelectedGroup = null;
            Stocks.Clear();

            var kindFilter   = BuildKindFilter();
            var activeFilter = ShowOnlyActive ? "AND g.is_active = TRUE" : "";

            var sql = $"""
                SELECT g.group_id, g.kind, g.name, g.rating,
                       COUNT(DISTINCT sgm.ticker) AS stock_count
                FROM groups g
                LEFT JOIN stock_group_map sgm ON g.group_id = sgm.group_id
                WHERE 1=1 {kindFilter} {activeFilter}
                GROUP BY g.group_id, g.kind, g.name, g.rating
                ORDER BY g.kind, g.name
                """;

            using var conn = OpenDb();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                Groups.Add(new GroupRow
                {
                    GroupId    = SafeInt(reader, "group_id"),
                    Kind       = SafeStr(reader, "kind"),
                    Name       = SafeStr(reader, "name"),
                    Rating     = SafeInt(reader, "rating"),
                    StockCount = SafeInt(reader, "stock_count"),
                });
            }
            StatusText = $"그룹 {Groups.Count}건";
        }
        catch (Exception ex) { StatusText = $"오류: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private string BuildKindFilter()
    {
        if (ShowSector && ShowTheme) return "";
        if (ShowSector)              return "AND g.kind = 'sector'";
        if (ShowTheme)               return "AND g.kind = 'theme'";
        return "AND 1=0";
    }

    // ──────────────────────────────────────────────────────────
    //  Group 선택 → Stocks 로드
    // ──────────────────────────────────────────────────────────
    partial void OnSelectedGroupChanged(GroupRow? value)
    {
        Stocks.Clear();
        SelectedStock = null;
        if (value is null) return;

        GroupSelected?.Invoke(value.GroupId);
        LoadStocksForGroup(value.GroupId);
    }

    private void LoadStocksForGroup(int groupId)
    {
        try
        {
            var activeFilter = ShowOnlyActive ? "AND s.is_active = TRUE" : "";
            var sql = $"""
                SELECT s.ticker, s.name, s.market, s.rating
                FROM stocks s
                JOIN stock_group_map sgm ON s.ticker = sgm.ticker
                WHERE sgm.group_id = {groupId} {activeFilter}
                ORDER BY s.ticker
                """;

            using var conn = OpenDb();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                Stocks.Add(new StockRow
                {
                    Ticker = SafeStr(reader, "ticker"),
                    Name   = SafeStr(reader, "name"),
                    Market = SafeStr(reader, "market"),
                    Rating = SafeInt(reader, "rating"),
                });
            }
            StatusText = $"{SelectedGroup?.Name}  {Stocks.Count}종목";
        }
        catch (Exception ex) { StatusText = $"오류: {ex.Message}"; }
    }

    // ──────────────────────────────────────────────────────────
    //  Stock 선택
    // ──────────────────────────────────────────────────────────
    partial void OnSelectedStockChanged(StockRow? value)
    {
        if (value is not null)
            StockSelected?.Invoke(value.Ticker);
    }

    // ──────────────────────────────────────────────────────────
    //  옵션 변경 시 자동 리로드
    // ──────────────────────────────────────────────────────────
    partial void OnShowOnlyActiveChanged(bool value) => LoadGroups();
    partial void OnShowSectorChanged(bool value)     => LoadGroups();
    partial void OnShowThemeChanged(bool value)      => LoadGroups();

    // ──────────────────────────────────────────────────────────
    //  Helper
    // ──────────────────────────────────────────────────────────
    private static DuckDBConnection OpenDb()
    {
        var conn = new DuckDBConnection($"Data Source={DbPath}");
        conn.Open();
        return conn;
    }

    private static string SafeStr(System.Data.IDataReader r, string col)
    {
        try { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? "" : r.GetValue(i)?.ToString() ?? ""; }
        catch { return ""; }
    }

    private static int SafeInt(System.Data.IDataReader r, string col)
    {
        try { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i)); }
        catch { return 0; }
    }
}
