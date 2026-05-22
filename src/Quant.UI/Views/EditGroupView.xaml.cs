using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;

using Quant.Core.Infrastructure;
using Quant.Core.Services;

using SkiaSharp;

using System.Collections.ObjectModel;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;

namespace Quant.UI.Views;

public partial class EditGroupView : UserControl
{
    public event Action<string, string>? StatusChanged;
    public event Action<string, string>? TickerDoubleClicked;  // ticker, name → MainWindow가 ChartView로 전달

    // ── LiveCharts bindings ───────────────────────────────────
    public ObservableCollection<ISeries> ChartSeries { get; } = [];
    public ObservableCollection<Axis>    ChartXAxes  { get; } = [];
    public ObservableCollection<Axis>    ChartYAxes  { get; } = [];

    // ── state ─────────────────────────────────────────────────
    private readonly DbManager _db;
    private DataTable? _groupTable;
    private int  _currentGroupId = -1;
    private int  _periodDays     = 365;
    private bool _downloading    = false;

    // ── color palette (Catppuccin Mocha) ─────────────────────
    private static readonly SKColor[] Palette =
    [
        SKColor.Parse("#89B4FA"), // blue
        SKColor.Parse("#A6E3A1"), // green
        SKColor.Parse("#F38BA8"), // red
        SKColor.Parse("#FAB387"), // peach
        SKColor.Parse("#F9E2AF"), // yellow
        SKColor.Parse("#CBA6F7"), // mauve
        SKColor.Parse("#89DCEB"), // sky
        SKColor.Parse("#94E2D5"), // teal
        SKColor.Parse("#EBA0AC"), // maroon
        SKColor.Parse("#B4BEFE"), // lavender
    ];

    // ── raw price data: ticker → (name, prices) ──────────────
    private Dictionary<string, (string name, List<(DateTime date, double close)> prices)> _rawData = [];

    // ── 현재 차트에 그려진 종목 (Crosshair 툴팁 필터) ────────
    private HashSet<string> _chartedTickers = [];

    public EditGroupView(DbManager db)
    {
        _db = db;
        InitializeComponent();
        //chkAllGroup.IsChecked = true;
        chkSectorGroup.IsChecked = true;
        chkThemeGroup.IsChecked = true;
        //groupKindFilter = "";
        DataContext = this;
        InitAxes();
        Loaded += (_, _) => LoadGroups();
    }

    // ═════════════════════════════════════════════════════════
    //  Axes
    // ═════════════════════════════════════════════════════════

    private void InitAxes()
    {
        ChartXAxes.Add(new Axis
        {
            Labeler = v =>
            {
                var ticks = (long)v;
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return "";
                return new DateTime(ticks).ToString("yy/MM/dd");
            },
            UnitWidth       = TimeSpan.FromDays(1).Ticks,
            LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6C7086")),
            SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
            TextSize        = 10,
        });
        ChartYAxes.Add(new Axis
        {
            Labeler         = v => $"{v:F1}",
            LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6C7086")),
            SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
            TextSize        = 10,
            Position        = LiveChartsCore.Measure.AxisPosition.End,
            MinLimit        = 0,
        });
    }

    // ═════════════════════════════════════════════════════════
    //  좌상: 그룹 Grid
    // ═════════════════════════════════════════════════════════

    //private string groupKindFilter = "";

    public void LoadGroups()
    {
        try
        {
            _groupTable = _db.Query_GroupList(chkSectorGroup.IsChecked, chkThemeGroup.IsChecked);
            //_groupTable = _db.Query(DbManager.GroupListSql(groupKindFilter));
            GridGroup.ItemsSource = _groupTable.DefaultView;

            var rowCount = $"{_groupTable.Rows.Count:N0}";
            GroupGridCount.Text = rowCount;
            Helpers.StatusSuccess(StatusChanged, $"LoadGroups= {rowCount}");
        }
        catch (Exception ex) { Helpers.StatusException(StatusChanged, ex, "LoadGroups"); }
    }

    //private string BuildGroupKindFilter()
    //{
    //    string kindFilter = "";
    //    if (chkSectorGroup.IsChecked != true)
    //        kindFilter += "AND g.kind != 'sector' ";
    //    if (chkThemeGroup.IsChecked != true)
    //        kindFilter += "AND g.kind != 'theme' ";
    //    return kindFilter.Length > 0 ? kindFilter : "";
    //}

    private void GroupFilter_Click(object sender, RoutedEventArgs e) => LoadGroups();
    //{
    //    groupKindFilter = BuildGroupKindFilter();
    //    LoadGroups();
    //}

    private void GridGroup_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridGroup.SelectedItem is not DataRowView row) return;
        if (!int.TryParse(row["group_id"]?.ToString(), out var gid)) return;
        _currentGroupId      = gid;
        TxtCurrGroupNameAtGrid.Text = TxtGroupNameAtChart.Text = row["name"]?.ToString() ?? "";
        GroupRatingCtrl.Rating = int.TryParse(row["rating"]?.ToString(), out var r0) ? Math.Clamp(r0, 0, 10) : 5;
        LoadTickers(gid);
        LoadChartData(gid);
    }

    // ═════════════════════════════════════════════════════════
    //  좌하: 티커 Grid
    // ═════════════════════════════════════════════════════════
    private void LoadTickers(int groupId)
    {
        try
        {
            /*            var excludeFilter = _db.BuildStockExcludeFilter("s", "c");

                        var dt = _db.Query($@"
                        SELECT
                            m.ticker,
                            s.name,

                            CASE s.market
                                WHEN 'KP' THEN 'KOSPI'
                                WHEN 'KQ' THEN 'KOSDAQ'
                                WHEN 'NYSE' THEN 'NYSE'
                                ELSE s.market
                            END AS market,

                            m.weight,

                            c.ret_1m,
                            c.ret_3m,
                            c.ret_6m,
                            c.ret_1y,

                            c.rs,

                            c.per,
                            c.pbr,
                            c.roe,

                            c.atr_percent,

                            c.distance_from_high,

                            c.volume_avg_20d

                        FROM stock_group_map m

                        JOIN stocks s
                            ON s.ticker = m.ticker

                        LEFT JOIN stock_cache c
                            ON c.ticker = m.ticker

                        WHERE
                            m.group_id = {groupId}
                            {excludeFilter}

                        ORDER BY
                            c.ret_1m DESC NULLS LAST
                    ");
            */

            var dt = _db.Query_StockList(groupId);
            if (!dt.Columns.Contains("chart_selected"))
            {
                var col = dt.Columns.Add("chart_selected", typeof(bool));
                col.DefaultValue = false;

                foreach (DataRow r in dt.Rows)
                    r["chart_selected"] = false;
            }

            GridTicker.ItemsSource = dt.DefaultView;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"티커 로드 오류: {ex.Message}";
        }
    }

    private void GridTicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 체크된 종목이 없을 때만 선택 종목으로 차트 갱신
        if (GridTicker.ItemsSource is not DataView dv || dv.Table == null) return;
        if (dv.Table.Columns.Contains("chart_selected") &&
            dv.Table.Rows.Cast<DataRow>().Any(r => r["chart_selected"] is true)) return;
        ApplyPeriod();
    }

    private void GridTicker_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GridTicker.SelectedItem is not DataRowView row) return;
        var ticker = row["ticker"]?.ToString() ?? "";
        var name   = row["name"]?.ToString()   ?? ticker;
        if (!string.IsNullOrEmpty(ticker))
            TickerDoubleClicked?.Invoke(ticker, name);
    }

    public void AddTicker(string ticker, string name)
    {
        if (CheckboxAddTicker.IsChecked == true)
        {
            try
            {
                _groupTable = _db.Query(
                    $@"INSERT INTO stock_group_map(ticker, group_id, weight) " +
                    $"VALUES('{ticker}', {_currentGroupId}, 5) " +
                    $"ON CONFLICT(ticker, group_id) DO UPDATE SET weight = EXCLUDED.weight");
                LoadTickers(_currentGroupId);
                TxtStatus.Text = $"AddTicker: _currentGroupId={_currentGroupId}, ticker={ticker}, name={name}";
            }
            catch (Exception ex) { Helpers.StatusException(StatusChanged, ex, "오류"); }
        }
    }

    // ═════════════════════════════════════════════════════════
    //  🔄 그룹 전체 다운로드
    // ═════════════════════════════════════════════════════════

    private async void BtnDownloadGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGroupId < 0) { TxtStatus.Text = "그룹을 먼저 선택하세요."; return; }
        if (_downloading)        { TxtStatus.Text = "다운로드 중입니다..."; return; }

        var memberDt = _db.Query(
            $"SELECT m.ticker, s.name FROM stock_group_map m " +
            $"JOIN stocks s ON s.ticker = m.ticker " +
            $"WHERE m.group_id = {_currentGroupId} ORDER BY m.ticker");

        if (memberDt.Rows.Count == 0) { TxtStatus.Text = "그룹에 속한 종목이 없습니다."; return; }

        var members = memberDt.Rows.Cast<DataRow>()
            .Select(r => (ticker: r["ticker"].ToString()!, name: r["name"].ToString()!))
            .ToList();

        _downloading = true;
        SetDownloadButtons(false);

        var svc      = new PriceDownloadService(_db);
        var rng      = new Random();
        int total    = members.Count;
        int done     = 0;
        int inserted = 0;
        int successCount = 0;
        var errors   = new List<string>();

        var progress = new Progress<string>(msg =>
        {
            TxtStatus.Text = msg;
            Helpers.StatusInfo(StatusChanged, msg);
        });

        try
        {
            foreach (var (ticker, name) in members)
            {
                done++;
                var prefix = $"[{done}/{total}] {ticker} ({name})";
                try
                {
                    var (cnt, _) = await svc.DownloadAsync(ticker, progress);
                    successCount++;
                    inserted += cnt;
                }
                catch (Exception ex)
                {
                    errors.Add(ticker);
                    var msg = $"{prefix}: 오류 — {ex.Message}";
                    TxtStatus.Text = msg;
                    Helpers.StatusException(StatusChanged, ex, prefix);
                }
                if (done < total)
                    await Task.Delay(rng.Next(1500, 2501));
            }

            if (successCount > 0)
                _db.RebuildStockCache();

            LoadChartData(_currentGroupId);
            var summary = errors.Count == 0
                ? $"다운로드 완료 — {total}개 종목, {inserted:N0}건 저장"
                : $"완료 — {total}개 중 {errors.Count}개 오류: {string.Join(", ", errors)}";
            TxtStatus.Text = summary;
            if (errors.Count == 0)
                Helpers.StatusSuccess(StatusChanged, summary);
            else
                Helpers.StatusWarning(StatusChanged, summary);
        }
        finally
        {
            _downloading = false;
            SetDownloadButtons(true);
        }
    }

    private void SetDownloadButtons(bool enabled)
    {
        BtnDownloadGroup.IsEnabled = enabled;
        BtnDownloadGroup.Content   = enabled ? "🔄" : "⏳";
    }

    // ═════════════════════════════════════════════════════════
    //  차트 데이터 로드
    // ═════════════════════════════════════════════════════════

    private void LoadChartData(int groupId)
    {
        try
        {
            if (GridTicker.ItemsSource is not DataView view || view.Table == null)
            {
                ChartSeries.Clear(); _rawData.Clear(); TxtChartInfo.Text = "종목 없음"; return;
            }
            var periodStart = _periodDays == 0
                ? "1900-01-01"
                : DateTime.Today.AddDays(-_periodDays).ToString("yyyy-MM-dd");

            var allDt = _db.Query(
                $"SELECT m.ticker, s.name, p.date, p.adj_close " +
                $"FROM stock_group_map m " +
                $"JOIN stocks s ON s.ticker = m.ticker " +
                $"JOIN daily_prices p ON p.ticker = m.ticker " +
                $"WHERE m.group_id = {groupId} AND p.date >= '{periodStart}' " +
                $"ORDER BY m.ticker, p.date");

            _rawData.Clear();
            foreach (DataRow r in allDt.Rows)
            {
                var ticker = r["ticker"].ToString()!;
                var name   = r["name"].ToString()!;
                DateTime.TryParse(r["date"]?.ToString(), out var d);
                double.TryParse(r["adj_close"]?.ToString(), out var p);
                if (d == default || p <= 0) continue;
                if (!_rawData.ContainsKey(ticker)) _rawData[ticker] = (name, []);
                _rawData[ticker].prices.Add((d, p));
            }

            //if (view.Table.Columns.Contains("chart_selected"))
            //{
            //    bool first = true;
            //    foreach (DataRow r in view.Table.Rows) { r["chart_selected"] = first; first = false; }
            //}

            ApplyPeriod();
            TxtStockCountAtGroupGrid.Text = $"{_rawData.Count}";
            TxtStatus.Text = $"[{TxtGroupNameAtChart.Text}] {_rawData.Count}개 종목";
            Helpers.StatusSuccess(StatusChanged, TxtStatus.Text);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"차트 오류: {ex.Message}";
            Helpers.StatusException(StatusChanged, ex, "차트 오류");
        }
    }

    // ═════════════════════════════════════════════════════════
    //  Base-100 정규화 & 렌더
    // ═════════════════════════════════════════════════════════

    private const string KospiTicker = "IDX_KOSPI";

    private void ApplyPeriod()
    {
        ChartSeries.Clear();
        _chartedTickers.Clear();
        if (_rawData.Count == 0) return;

        var checkedTickers = new HashSet<string>();
        if (GridTicker.ItemsSource is DataView dv && dv.Table != null && dv.Table.Rows.Count > 0)
        {
            if (dv.Table.Columns.Contains("chart_selected"))
                foreach (DataRow r in dv.Table.Rows)
                    if (r["chart_selected"] is true) checkedTickers.Add(r["ticker"].ToString()!);

            // 체크된 종목이 없으면 GridTicker 선택(selected) 종목을 그림
            if (checkedTickers.Count == 0)
            {
                var fallbackTicker =
                    (GridTicker.SelectedItem as DataRowView)?["ticker"]?.ToString()
                    ?? dv.Table.Rows[0]["ticker"]?.ToString();
                if (!string.IsNullOrWhiteSpace(fallbackTicker) && _rawData.ContainsKey(fallbackTicker))
                    checkedTickers.Add(fallbackTicker);
            }
        }

        int colorIdx = 0;
        var lastValues = new List<(string label, double last)>();

        foreach (var (ticker, (name, prices)) in _rawData)
        {
            if (!checkedTickers.Contains(ticker) || prices.Count < 2) continue;
            var baseClose = prices.First().close;
            var points = prices
                .Select(d => new DateTimePoint(d.date, Math.Round(d.close / baseClose * 100.0, 4)))
                .ToList();
            var color = Palette[colorIdx++ % Palette.Length];
            ChartSeries.Add(new LineSeries<DateTimePoint>
            {
                Values = new ObservableCollection<DateTimePoint>(points),
                Stroke = new SolidColorPaint(color) { StrokeThickness = 1.5f },
                Fill = null, GeometrySize = 0, GeometryFill = null, GeometryStroke = null,
                LineSmoothness = 0, Name = name,
            });
            _chartedTickers.Add(ticker);
            lastValues.Add((name, points.Last().Value ?? 100.0));
        }

        AddKospiOverlay();

        if (lastValues.Count > 0)
        {
            var best  = lastValues.MaxBy(x => x.last);
            var worst = lastValues.MinBy(x => x.last);
            TxtChartInfo.Text = $"▲ {best.label}  {best.last - 100:+0.0;-0.0}%   ▼ {worst.label}  {worst.last - 100:+0.0;-0.0}%";
        }

        // X축 범위 수동 설정 (AnimationsSpeed="0" 사용 시 자동 스케일링 지연 방지)
        var xAxis = ChartXAxes[0];
        var xValues = ChartSeries
            .OfType<LineSeries<DateTimePoint>>()
            .SelectMany(s => s.Values ?? [])
            .Select(p => (double)p.DateTime.Ticks)
            .ToList();

        if (xValues.Count > 0)
        {
            xAxis.MinLimit = xValues.Min();
            xAxis.MaxLimit = xValues.Max();
        }
        else
        {
            xAxis.MinLimit = null;
            xAxis.MaxLimit = null;
        }

        var yAxis = ChartYAxes[0];
        var allValues = ChartSeries
            .OfType<LineSeries<DateTimePoint>>()
            .SelectMany(s => s.Values ?? [])
            .Select(p => p.Value).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (allValues.Count > 0)
        {
            var margin = (allValues.Max() - allValues.Min()) * 0.03;
            yAxis.MinLimit = Math.Max(0, allValues.Min() - margin);
            yAxis.MaxLimit = allValues.Max() + margin;
        }
        else { yAxis.MinLimit = null; yAxis.MaxLimit = null; }
    }

    private void AddKospiOverlay()
    {
        var periodStart = _periodDays == 0 ? "1900-01-01"
            : DateTime.Today.AddDays(-_periodDays).ToString("yyyy-MM-dd");
        List<(DateTime date, double close)> prices;
        if (_rawData.TryGetValue(KospiTicker, out var cached))
            prices = cached.prices;
        else
        {
            try
            {
                var dt = _db.Query(
                    $"SELECT date, adj_close FROM daily_prices " +
                    $"WHERE ticker = '{KospiTicker}' AND date >= '{periodStart}' ORDER BY date ASC");
                prices = dt.Rows.Cast<DataRow>().Select(r =>
                {
                    DateTime.TryParse(r[0]?.ToString(), out var d);
                    double.TryParse(r[1]?.ToString(), out var p);
                    return (date: d, close: p);
                }).Where(x => x.date != default && x.close > 0).ToList();
            }
            catch { return; }
        }
        if (prices.Count < 2) return;
        var baseClose = prices.First().close;
        var points = prices.Select(d => new DateTimePoint(d.date, Math.Round(d.close / baseClose * 100.0, 4))).ToList();
        ChartSeries.Add(new LineSeries<DateTimePoint>
        {
            Values = new ObservableCollection<DateTimePoint>(points),
            Stroke = new SolidColorPaint(SKColor.Parse("#585B70")) { StrokeThickness = 1f },
            Fill = null, GeometrySize = 0, GeometryFill = null, GeometryStroke = null,
            LineSmoothness = 0, Name = "KOSPI",
        });
    }

    // ═════════════════════════════════════════════════════════
    //  Buttons & CRUD
    // ═════════════════════════════════════════════════════════

    private void BtnPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        _periodDays = int.Parse(btn.Tag?.ToString() ?? "365");
        foreach (var b in new[] { Btn1M, Btn3M, Btn6M, Btn1Y, Btn2Y, BtnAll })
            b.Foreground = System.Windows.Media.Brushes.Gray;
        btn.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#89B4FA"));
        if (_currentGroupId >= 0) LoadChartData(_currentGroupId);
    }

    private void BtnChart_Click(object sender, RoutedEventArgs e)
    {
        if (GridTicker.ItemsSource is not DataView view || sender is not Button btn) return;

        if (view.Table?.Columns.Contains("chart_selected") != true) return;

        foreach (DataRow row in view.Table.Rows) row["chart_selected"] = false;

        var tag = btn.Tag?.ToString() ?? "";
        if (tag == "1")
        {
            foreach (DataRow row in view.Table.Rows) row["chart_selected"] = true;
            return;
        }

        if (tag == "10")
        {
            var count = Math.Min(10, view.Count);
            for (var i = 0; i < count; i++)
                view[i]["chart_selected"] = true;
        }
    }

    private void BtnRemoveTicker_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGroupId < 0) { TxtStatus.Text = "그룹을 먼저 선택하세요."; return; }
        if (GridTicker.SelectedItem is not DataRowView row) { TxtStatus.Text = "티커를 선택하세요."; return; }
        var ticker = row["ticker"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(ticker)) return;
        if (MessageBox.Show($"[{ticker}]을 그룹에서 제거하시겠습니까?",
                "제거 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        try
        {
            using var conn = _db.OpenNativeConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM stock_group_map WHERE group_id = $1 AND ticker = $2";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = _currentGroupId });
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
            cmd.ExecuteNonQuery();
                
                _rawData.Remove(ticker);
            LoadTickers(_currentGroupId);
                ApplyPeriod();
                UpdateGroupCount();
                
            TxtStatus.Text = $"제거됨: {ticker}";
            Helpers.StatusInfo(StatusChanged, $"제거됨: {ticker}");
        }
        catch (Exception ex) { TxtStatus.Text = $"제거 오류: {ex.Message}"; }
    }

    //private void BtnDeleteTicker_Click(object sender, RoutedEventArgs e)
    //{
    //    if (_currentGroupId < 0) return;
    //    if (sender is not Button btn || btn.Tag is not DataRowView row) return;
    //    var ticker = row["ticker"]?.ToString() ?? "";
    //    if (string.IsNullOrEmpty(ticker)) return;
    //    if (MessageBox.Show($"[{ticker}]을 그룹에서 제거하시겠습니까?",
    //            "제거 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning)
    //        != MessageBoxResult.Yes) return;
    //    try
    //    {
    //        using var conn = _db.OpenNativeConnection();
    //        using var cmd  = conn.CreateCommand();
    //        cmd.CommandText = "DELETE FROM stock_group_map WHERE group_id = $1 AND ticker = $2";
    //        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = _currentGroupId });
    //        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
    //        cmd.ExecuteNonQuery();
    //        _rawData.Remove(ticker);
    //        LoadTickers(_currentGroupId);
    //        ApplyPeriod();
    //        UpdateGroupCount();
    //        TxtStatus.Text = $"제거됨: {ticker}";
    //        StatusChanged?.Invoke($"제거됨: {ticker}", "#F38BA8");
    //    }
    //    catch (Exception ex) { TxtStatus.Text = $"제거 오류: {ex.Message}"; }
    //}

    private void GroupRatingCtrl_RatingChanged(int rating)
    {
        if (_currentGroupId < 0) return;
        try
        {
            _db.SetGroupRating(rating, _currentGroupId);
            if (_groupTable != null && GridGroup.SelectedItem is DataRowView row)
                row.Row["rating"] = rating;
            Helpers.StatusSuccess(StatusChanged, $"[{TxtGroupNameAtChart.Text}] rating 저장: {rating}");
        }
        catch (Exception ex) { Helpers.StatusException(StatusChanged, ex, "rating 저장 오류"); }
    }

    private void UpdateGroupCount()
    {
        if (_groupTable == null || GridGroup.SelectedItem is not DataRowView selRow) return;
        if (!int.TryParse(selRow["group_id"]?.ToString(), out var gid)) return;
        try
        {
            var cnt = _db.Scalar<long>($"SELECT COUNT(*) FROM stock_group_map WHERE group_id = {gid}");
            foreach (DataRow r in _groupTable.Rows)
                if (r["group_id"].ToString() == gid.ToString()) { r["count"] = cnt; break; }
        }
        catch { }
    }

    private void ChartSelected_Changed(object sender, RoutedEventArgs e) => ApplyPeriod();

    private void WeightEdit_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            GridTicker.CommitEdit(DataGridEditingUnit.Row, true);
        else if (e.Key == System.Windows.Input.Key.Escape)
            GridTicker.CancelEdit();
    }

    private void GridTicker_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Column.Header?.ToString() != "w") return;
        if (e.Row.Item is not DataRowView row) return;

        var ticker = row["ticker"]?.ToString() ?? "";

        // DataGridTemplateColumn의 EditingElement는 ContentPresenter → 그 안의 TextBox를 찾아야 함
        var presenter = e.EditingElement as ContentPresenter;
        var tb = presenter?.ContentTemplate?.FindName("", presenter) as TextBox
                 ?? FindVisualChild<TextBox>(e.EditingElement);
        if (tb == null || !int.TryParse(tb.Text, out var w)) return;
        w = Math.Clamp(w, 1, 10);

        try
        {
            _db.Execute($"UPDATE stock_group_map SET weight = {w} " +
                        $"WHERE group_id = {_currentGroupId} AND ticker = '{ticker}'");
            row["weight"] = w;
            LoadGroups();
            Helpers.StatusSuccess(StatusChanged, $"{ticker} weight → {w}");
        }
        catch (Exception ex) { Helpers.StatusException(StatusChanged, ex, "weight 저장 오류"); }
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    // ═════════════════════════════════════════════════════════
    //  Crosshair + Tooltip  (ChartView 방식 이식)
    //
    //  X축: DateTimePoint → DateTime.Ticks (long) 사용.
    //  픽셀 → Ticks 변환:
    //    plotW = ChartAreaGrid.ActualWidth - rightMargin(Y축 라벨 예약폭)
    //    xMin/xMax = ChartXAxes[0].MinLimit / MaxLimit (Ticks 단위)
    //    ticksAtCursor = xMin + (cx / plotW) * (xMax - xMin)
    //  → ticksAtCursor 를 DateTime 으로 변환 후 _rawData에서 근접일 조회.
    // ═════════════════════════════════════════════════════════
    private void ChartAreaGrid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // ChartAreaGrid 기준 마우스 위치
        var pos = e.GetPosition(ChartAreaGrid);

        // Row 0 = 헤더(28px), Row 1 = 차트 본체
        double headerH   = ChartAreaGrid.RowDefinitions[0].ActualHeight;
        double chartH    = ChartAreaGrid.RowDefinitions[1].ActualHeight;
        double chartW    = ChartAreaGrid.ActualWidth;

        // 마우스가 차트 본체 안에 있는지 확인
        if (pos.Y < headerH || pos.Y > headerH + chartH)
        {
            EgCrosshairCanvas.Visibility = Visibility.Collapsed;
            EgTooltipPanel.Visibility    = Visibility.Collapsed;
            return;
        }

        EgCrosshairCanvas.Visibility = Visibility.Visible;

        // Canvas는 Row 1 기준 → Canvas Y = pos.Y - headerH
        double cy = pos.Y - headerH;
        double cx = pos.X;

        // 세로선
        EgCrossV.X1 = cx; EgCrossV.Y1 = 0;
        EgCrossV.X2 = cx; EgCrossV.Y2 = chartH;
        // 가로선
        EgCrossH.X1 = 0;      EgCrossH.Y1 = cy;
        EgCrossH.X2 = chartW; EgCrossH.Y2 = cy;

        // ── Ticks → DateTime 변환 ──────────────────────────
        if (ChartXAxes.Count == 0 || _rawData.Count == 0)
        {
            EgTooltipPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // Y축 라벨 영역(우측) 예약폭 — ChartView SyncDrawMargin Right=52 에 맞춤
        const double rightMargin = 52.0;
        double plotW = chartW - rightMargin;
        if (plotW <= 0) { EgTooltipPanel.Visibility = Visibility.Collapsed; return; }

        var xAxis = ChartXAxes[0];
        double xMin = xAxis.MinLimit ?? double.MinValue;
        double xMax = xAxis.MaxLimit ?? double.MaxValue;

        // MinLimit/MaxLimit 이 null 이면 전체 데이터 범위로 추정
        if (xMin == double.MinValue || xMax == double.MaxValue)
        {
            var allDates = _rawData.Values
                .SelectMany(v => v.prices)
                .Select(p => p.date.Ticks)
                .ToList();
            if (allDates.Count == 0) { EgTooltipPanel.Visibility = Visibility.Collapsed; return; }
            xMin = allDates.Min();
            xMax = allDates.Max();
        }

        double xRange = xMax - xMin;
        if (xRange <= 0) { EgTooltipPanel.Visibility = Visibility.Collapsed; return; }

        double ticksAtCursor = xMin + (cx / plotW) * xRange;
        var targetDate = new DateTime((long)Math.Round(ticksAtCursor)).Date;

        // ── 각 종목의 해당 날짜(또는 직전 영업일) 값 조회 ──
        // ── 각 종목 데이터 수집 ───────────────────────────────
        var tooltipRows = new List<(string name, double rel, SKColor color)>();
        int colorIdx = 0;
        foreach (var (ticker, (name, prices)) in _rawData)
        {
            var color = Palette[colorIdx % Palette.Length];
            colorIdx++;
            if (!_chartedTickers.Contains(ticker) || prices.Count < 2) continue;

            var match = prices
                .Where(p => p.date.Date <= targetDate)
                .OrderByDescending(p => p.date)
                .FirstOrDefault();
            if (match == default) continue;

            var baseClose = prices.First().close;
            double rel    = Math.Round(match.close / baseClose * 100.0, 2);
            tooltipRows.Add((name, rel, color));
        }

        if (tooltipRows.Count == 0) { EgTooltipPanel.Visibility = Visibility.Collapsed; return; }

        // ── 상승률 내림차순 정렬 ─────────────────────────────
        tooltipRows.Sort((a, b) => b.rel.CompareTo(a.rel));

        // ── tooltip 렌더 ─────────────────────────────────────
        EgTtDate.Text = targetDate.ToString("yyyy-MM-dd (ddd)");
        EgTtRows.Children.Clear();
        foreach (var (name, rel, color) in tooltipRows)
        {
            double diff = rel - 100.0;
            var wpfColor = System.Windows.Media.Color.FromRgb(color.Red, color.Green, color.Blue);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text       = "● ",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 10,
                Foreground = new System.Windows.Media.SolidColorBrush(wpfColor),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text       = $"{name,-12}  {rel,7:F2}  ({diff:+0.00;-0.00}%)",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 10,
                Foreground = new System.Windows.Media.SolidColorBrush(wpfColor),
                VerticalAlignment = VerticalAlignment.Center,
            });
            EgTtRows.Children.Add(row);
        }

        EgTooltipPanel.Visibility = Visibility.Visible;
    }

    private void ChartAreaGrid_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        EgCrosshairCanvas.Visibility = Visibility.Collapsed;
        ShowEgTooltipAtLastDate();
    }

    // 마우스가 차트 밖일 때 최신 날짜(today 기준) 툴팁 고정 표시
    private void ShowEgTooltipAtLastDate()
    {
        if (_rawData.Count == 0 || _chartedTickers.Count == 0)
        {
            EgTooltipPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // 가장 최근 공통 날짜 = 차트된 종목들의 마지막 날짜 중 최솟값
        var targetDate = _rawData
            .Where(kv => _chartedTickers.Contains(kv.Key) && kv.Value.prices.Count > 0)
            .Select(kv => kv.Value.prices.Last().date.Date)
            .DefaultIfEmpty(DateTime.Today)
            .Min();

        var tooltipRows = new List<(string name, double rel, SKColor color)>();
        int colorIdx = 0;
        foreach (var (ticker, (name, prices)) in _rawData)
        {
            var color = Palette[colorIdx % Palette.Length];
            colorIdx++;
            if (!_chartedTickers.Contains(ticker) || prices.Count < 2) continue;

            var match = prices
                .Where(p => p.date.Date <= targetDate)
                .OrderByDescending(p => p.date)
                .FirstOrDefault();
            if (match == default) continue;

            var baseClose = prices.First().close;
            double rel    = Math.Round(match.close / baseClose * 100.0, 2);
            tooltipRows.Add((name, rel, color));
        }

        if (tooltipRows.Count == 0) { EgTooltipPanel.Visibility = Visibility.Collapsed; return; }

        tooltipRows.Sort((a, b) => b.rel.CompareTo(a.rel));

        EgTtDate.Text = targetDate.ToString("yyyy-MM-dd (ddd)");
        EgTtRows.Children.Clear();
        foreach (var (name, rel, color) in tooltipRows)
        {
            double diff = rel - 100.0;
            var wpfColor = System.Windows.Media.Color.FromRgb(color.Red, color.Green, color.Blue);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text       = "● ",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 10,
                Foreground = new System.Windows.Media.SolidColorBrush(wpfColor),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text       = $"{name,-12}  {rel,7:F2}  ({diff:+0.00;-0.00}%)",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 10,
                Foreground = new System.Windows.Media.SolidColorBrush(wpfColor),
                VerticalAlignment = VerticalAlignment.Center,
            });
            EgTtRows.Children.Add(row);
        }

        EgTooltipPanel.Visibility = Visibility.Visible;
    }

    private void BtnNewGroup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new GroupEditDialog(null);
        if (dlg.ShowDialog() == true) LoadGroups();
    }

    private void BtnEditGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GridGroup.SelectedItem is not DataRowView row) { TxtStatus.Text = "그룹을 선택하세요."; return; }
        var dlg = new GroupEditDialog(row);
        if (dlg.ShowDialog() == true) LoadGroups();
    }

    private void BtnDeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GridGroup.SelectedItem is not DataRowView row) { TxtStatus.Text = "그룹을 선택하세요."; return; }
        var name = row["name"]?.ToString() ?? "";
        var id   = row["group_id"]?.ToString() ?? "0";
        if (MessageBox.Show($"그룹 [{name}]을 삭제하시겠습니까?",
                "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        try
        {
            _db.Execute($"DELETE FROM groups WHERE group_id = {id}");
            Helpers.StatusError(StatusChanged, $"삭제됨: {name}");
            _currentGroupId = -1;
            TxtStockCountAtGroupGrid.Text =
            TxtCurrGroupNameAtGrid.Text = TxtCurrGroupNameAtGrid.Text = TxtChartInfo.Text = "";
            GridTicker.ItemsSource = null;
            ChartSeries.Clear(); _rawData.Clear();
            LoadGroups();
        }
        catch (Exception ex) { Helpers.StatusException(StatusChanged, ex, "삭제 오류"); }
    }
}
