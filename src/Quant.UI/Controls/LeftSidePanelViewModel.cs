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
}

// ──────────────────────────────────────────────────────────────
//  Stock row
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
    private readonly DbManager _db = DbManager.Instance;

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

    // ── 이벤트 ───────────────────────────────────────────────
    public event Action<string, string>? StockSelected;
    public event Action<int, string>?    GroupSelected;
    public event Action<string, string>? IndicatorSelected;  // (symbol, label)

    // ═════════════════════════════════════════════════════════
    public LeftSidePanelViewModel()
    {
        InitIndicators();
    }

    private void InitIndicators()
    {
        // IndicatorDefs 순서대로 빈 항목 생성 후 DB 최신값으로 채움
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

    /// <summary>GLOBAL INDICATORS 항목 클릭 시 ChartView 로 이동</summary>
    public void SelectIndicator(GlobalIndicatorItem item)
        => IndicatorSelected?.Invoke(item.Symbol, item.Label);

    // ──────────────────────────────────────────────────────────
    //  Groups 로드
    // ──────────────────────────────────────────────────────────
    [RelayCommand]
    public async Task DownloadIndicatorsAsync()
    {
        if (IsBusy) return;
        IsBusy     = true;
        StatusText = "인디케이터 다운로드 중…";
        try
        {
            var svc      = new IndicatorDownloadService(_db);
            // Progress 콜백을 UI 스레드에서 실행
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

            var dt = _db.Query(sql);
            foreach (DataRow row in dt.Rows)
            {
                Groups.Add(new GroupRow
                {
                    GroupId    = SafeInt(row, "group_id"),
                    Kind       = SafeStr(row, "kind"),
                    Name       = SafeStr(row, "name"),
                    Rating     = SafeInt(row, "rating"),
                    StockCount = SafeInt(row, "stock_count"),
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
        GroupSelected?.Invoke(value.GroupId, value.Name);
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

            var dt = _db.Query(sql);
            foreach (DataRow row in dt.Rows)
            {
                Stocks.Add(new StockRow
                {
                    Ticker = SafeStr(row, "ticker"),
                    Name   = SafeStr(row, "name"),
                    Market = SafeStr(row, "market"),
                    Rating = SafeInt(row, "rating"),
                });
            }
            StatusText = $"{SelectedGroup?.Name}  {Stocks.Count}종목";
        }
        catch (Exception ex) { StatusText = $"오류: {ex.Message}"; }
    }

    partial void OnSelectedStockChanged(StockRow? value)
    {
        if (value is not null) StockSelected?.Invoke(value.Ticker, value.Name);
    }

    partial void OnShowOnlyActiveChanged(bool value) => LoadGroups();
    partial void OnShowSectorChanged(bool value)     => LoadGroups();
    partial void OnShowThemeChanged(bool value)      => LoadGroups();

    // ──────────────────────────────────────────────────────────
    //  Helper — DataRow에서 안전 추출 (DataTable 기반으로 전환)
    // ──────────────────────────────────────────────────────────
    private static string SafeStr(DataRow r, string col)
    {
        try { return r.IsNull(col) ? "" : r[col]?.ToString() ?? ""; }
        catch { return ""; }
    }

    private static int SafeInt(DataRow r, string col)
    {
        try { return r.IsNull(col) ? 0 : Convert.ToInt32(r[col]); }
        catch { return 0; }
    }
}
