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
using System.Xml.Linq;

namespace Quant.UI.Views;

public partial class EditGroupView : UserControl
{
    public event Action<string, string>? StatusChanged;

    // ── LiveCharts bindings ───────────────────────────────────
    public ObservableCollection<ISeries> ChartSeries { get; } = [];
    public ObservableCollection<Axis>    ChartXAxes  { get; } = [];
    public ObservableCollection<Axis>    ChartYAxes  { get; } = [];

    // ── state ─────────────────────────────────────────────────
    private readonly DbManager _db = DbManager.Instance;
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
    // name은 tooltip에 사용
    private Dictionary<string, (string name, List<(DateTime date, double close)> prices)> _rawData = [];

    // ─────────────────────────────────────────────────────────

    public EditGroupView()
    {
        InitializeComponent();
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

    public void LoadGroups()
    {
        try
        {
            _groupTable = _db.Query(
                "SELECT g.group_id, g.kind, g.name, g.description, g.rating, g.is_active, " +
                "COUNT(m.ticker) AS count " +
                "FROM groups g " +
                "LEFT JOIN stock_group_map m ON m.group_id = g.group_id " +
                "GROUP BY g.group_id, g.kind, g.name, g.description, g.rating, g.is_active " +
                "ORDER BY g.kind, g.name");
            GridGroup.ItemsSource = _groupTable.DefaultView;
            TxtRowCount.Text = $"{_groupTable.Rows.Count:N0} groups";
            StatusChanged?.Invoke($"그룹 {_groupTable.Rows.Count}건", "#A6E3A1");
        }
        catch (Exception ex) { StatusChanged?.Invoke($"오류: {ex.Message}", "#F38BA8"); }
    }

    private void GridGroup_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridGroup.SelectedItem is not DataRowView row) return;
        if (!int.TryParse(row["group_id"]?.ToString(), out var gid)) return;

        _currentGroupId      = gid;
        TxtGroupName.Text    = row["name"]?.ToString() ?? "";
        TxtTickerHeader.Text = $"TICKERS — {TxtGroupName.Text}";

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
            var dt = _db.Query(
                $"WITH latest AS ( " +
                $"  SELECT ticker, adj_close, date, " +
                $"    ROW_NUMBER() OVER (PARTITION BY ticker ORDER BY date DESC) AS rn " +
                $"  FROM daily_prices " +
                $"), " +
                $"price_1m AS ( " +
                $"  SELECT ticker, adj_close, " +
                $"    ROW_NUMBER() OVER (PARTITION BY ticker ORDER BY ABS(DATEDIFF('day', date, CURRENT_DATE - INTERVAL 1 MONTH))) AS rn " +
                $"  FROM daily_prices " +
                $"  WHERE date BETWEEN CURRENT_DATE - INTERVAL 1 MONTH - INTERVAL 5 DAY " +
                $"                 AND CURRENT_DATE - INTERVAL 1 MONTH + INTERVAL 5 DAY " +
                $"), " +
                $"price_3m AS ( " +
                $"  SELECT ticker, adj_close, " +
                $"    ROW_NUMBER() OVER (PARTITION BY ticker ORDER BY ABS(DATEDIFF('day', date, CURRENT_DATE - INTERVAL 3 MONTH))) AS rn " +
                $"  FROM daily_prices " +
                $"  WHERE date BETWEEN CURRENT_DATE - INTERVAL 3 MONTH - INTERVAL 5 DAY " +
                $"                 AND CURRENT_DATE - INTERVAL 3 MONTH + INTERVAL 5 DAY " +
                $") " +
                $"SELECT m.ticker, s.name, s.market, m.weight, " +
                $"  ROUND((l.adj_close - p1.adj_close) / p1.adj_close * 100, 2) AS ret_1m, " +
                $"  ROUND((l.adj_close - p3.adj_close) / p3.adj_close * 100, 2) AS ret_3m " +
                $"FROM stock_group_map m " +
                $"JOIN stocks s ON s.ticker = m.ticker " +
                $"LEFT JOIN latest   l  ON l.ticker  = m.ticker AND l.rn  = 1 " +
                $"LEFT JOIN price_1m p1 ON p1.ticker = m.ticker AND p1.rn = 1 " +
                $"LEFT JOIN price_3m p3 ON p3.ticker = m.ticker AND p3.rn = 1 " +
                $"WHERE m.group_id = {groupId} " +
                $"ORDER BY ret_1m DESC NULLS LAST");

            if (!dt.Columns.Contains("chart_selected"))
            {
                var col = dt.Columns.Add("chart_selected", typeof(bool));
                col.DefaultValue = false;
                foreach (DataRow r in dt.Rows) r["chart_selected"] = false;
            }
            GridTicker.ItemsSource = dt.DefaultView;
        }
        catch (Exception ex) { TxtStatus.Text = $"티커 로드 오류: {ex.Message}"; }
    }

    private void GridTicker_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

	public void AddTicker(string ticker, string name)
	{
		if (CheckboxAddTicker.IsChecked == true)
		{
			try
			{
				_groupTable = _db.Query(
					$@"INSERT INTO stock_group_map(ticker, group_id, weight) " +
                    $"VALUES('{ticker}', {_currentGroupId}, 5) " +
                    $"ON CONFLICT(ticker, group_id) DO UPDATE SET weight = EXCLUDED.weight"
					);
				LoadTickers(_currentGroupId);
				TxtStatus.Text = $"AddTicker: _currentGroupId={_currentGroupId}, ticker={ticker}, name={name}";
			}
			catch (Exception ex) { StatusChanged?.Invoke($"오류: {ex.Message}", "#F38BA8"); }


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
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM stock_group_map WHERE group_id = $1 AND ticker = $2";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = _currentGroupId });
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
            cmd.ExecuteNonQuery();

            LoadTickers(_currentGroupId);
            LoadChartData(_currentGroupId);
            TxtStatus.Text = $"제거됨: {ticker}";
            StatusChanged?.Invoke($"제거됨: {ticker}", "#F38BA8");
        }
        catch (Exception ex) { TxtStatus.Text = $"제거 오류: {ex.Message}"; }
    }

    // ═════════════════════════════════════════════════════════
    //  🔄 그룹 전체 다운로드
    // ═════════════════════════════════════════════════════════

    private async void BtnDownloadGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGroupId < 0) { TxtStatus.Text = "그룹을 먼저 선택하세요."; return; }
        if (_downloading)        { TxtStatus.Text = "다운로드 중입니다..."; return; }

        // 그룹 내 ticker + name 목록 수집
        var memberDt = _db.Query(
            $"SELECT m.ticker, s.name FROM stock_group_map m " +
            $"JOIN stocks s ON s.ticker = m.ticker " +
            $"WHERE m.group_id = {_currentGroupId} ORDER BY m.ticker");

        if (memberDt.Rows.Count == 0) { TxtStatus.Text = "그룹에 속한 종목이 없습니다."; return; }

        var members = memberDt.Rows.Cast<DataRow>()
            .Select(r => (ticker: r["ticker"].ToString()!, name: r["name"].ToString()!))
            .ToList();

        // UI 잠금
        _downloading = true;
        SetDownloadButtons(false);

        var svc      = new PriceDownloadService(_db);
        var rng      = new Random();
        int total    = members.Count;
        int done     = 0;
        int inserted = 0;
        var errors   = new List<string>();

        var progress = new Progress<string>(msg =>
        {
            TxtStatus.Text = msg;
            StatusChanged?.Invoke(msg, "#89B4FA");
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
                    inserted += cnt;
                }
                catch (Exception ex)
                {
                    errors.Add(ticker);
                    var msg = $"{prefix}: 오류 — {ex.Message}";
                    TxtStatus.Text = msg;
                    StatusChanged?.Invoke(msg, "#F38BA8");
                }

                // rate limit 방지: 마지막 ticker가 아닐 때만 대기
                if (done < total)
                {
                    // 1500~2500ms 랜덤 delay (Yahoo Finance 차단 임계값 회피)
                    var delay = rng.Next(1500, 2501);
                    await Task.Delay(delay);
                }
            }

            // 완료 후 차트 갱신
            LoadChartData(_currentGroupId);

            var summary = errors.Count == 0
                ? $"다운로드 완료 — {total}개 종목, {inserted:N0}건 저장"
                : $"완료 — {total}개 중 {errors.Count}개 오류: {string.Join(", ", errors)}";

            TxtStatus.Text = summary;
            StatusChanged?.Invoke(summary, errors.Count == 0 ? "#A6E3A1" : "#F9E2AF");
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
    //  우하: 차트 데이터 로드
    // ═════════════════════════════════════════════════════════

    // groupId 파라미터 오버로드: 그룹 전환 시 전체 _rawData 재로드
    private void LoadChartData(int groupId)
    {
        try
        {
            if (GridTicker.ItemsSource is not DataView view || view.Table == null)
            {
                ChartSeries.Clear();
                _rawData.Clear();
                TxtChartInfo.Text = "종목 없음";
                return;
            }

            // 기간 필터를 DB에서 처리 — N+1 쿼리 → 단일 쿼리
            var periodStart = _periodDays == 0
                ? "1900-01-01"
                : DateTime.Today.AddDays(-_periodDays).ToString("yyyy-MM-dd");

            var allDt = _db.Query(
                $"SELECT m.ticker, s.name, p.date, p.adj_close " +
                $"FROM stock_group_map m " +
                $"JOIN stocks s ON s.ticker = m.ticker " +
                $"JOIN daily_prices p ON p.ticker = m.ticker " +
                $"WHERE m.group_id = {groupId} " +
                $"  AND p.date >= '{periodStart}' " +
                $"ORDER BY m.ticker, p.date");

            _rawData.Clear();
            foreach (DataRow r in allDt.Rows)
            {
                var ticker = r["ticker"].ToString()!;
                var name   = r["name"].ToString()!;
                DateTime.TryParse(r["date"]?.ToString(), out var d);
                double.TryParse(r["adj_close"]?.ToString(), out var p);
                if (d == default || p <= 0) continue;

                if (!_rawData.ContainsKey(ticker))
                    _rawData[ticker] = (name, []);
                _rawData[ticker].prices.Add((d, p));
            }

            // 최초 로드 시 첫 번째 항목만 선택
            if (view.Table.Columns.Contains("chart_selected"))
            {
                bool first = true;
                foreach (DataRow r in view.Table.Rows)
                {
                    r["chart_selected"] = first;
                    first = false;
                }
            }

            ApplyPeriod();
            TxtStatus.Text = $"[{TxtGroupName.Text}] {_rawData.Count}개 종목";
            StatusChanged?.Invoke(TxtStatus.Text, "#A6E3A1");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"차트 오류: {ex.Message}";
            StatusChanged?.Invoke(TxtStatus.Text, "#F38BA8");
        }
    }

    // ═════════════════════════════════════════════════════════
    //  Base-100 정규화 & 렌더
    // ═════════════════════════════════════════════════════════

    private const string KospiTicker = "IDX_KOSPI";

    private void ApplyPeriod()
    {
        ChartSeries.Clear();
        if (_rawData.Count == 0) return;

        // checked ticker 목록 수집
        var checkedTickers = new HashSet<string>();
        if (GridTicker.ItemsSource is DataView dv && dv.Table != null
            && dv.Table.Columns.Contains("chart_selected"))
        {
            foreach (DataRow r in dv.Table.Rows)
                if (r["chart_selected"] is true)
                    checkedTickers.Add(r["ticker"].ToString()!);
        }
        if (checkedTickers.Count == 0)
            checkedTickers = _rawData.Keys.ToHashSet();

        int colorIdx = 0;
        var lastValues = new List<(string label, double last)>();

        foreach (var (ticker, (name, prices)) in _rawData)
        {
            if (!checkedTickers.Contains(ticker)) continue;
            if (prices.Count < 2) continue;

            var baseClose = prices.First().close;
            var points = prices
                .Select(d => new DateTimePoint(d.date, Math.Round(d.close / baseClose * 100.0, 4)))
                .ToList();

            var color = Palette[colorIdx % Palette.Length];
            colorIdx++;

            // Name에 종목명 사용 → tooltip에 표시됨
            ChartSeries.Add(new LineSeries<DateTimePoint>
            {
                Values         = new ObservableCollection<DateTimePoint>(points),
                Stroke         = new SolidColorPaint(color) { StrokeThickness = 1.5f },
                Fill           = null,
                GeometrySize   = 0,
                GeometryFill   = null,
                GeometryStroke = null,
                LineSmoothness = 0,
                Name           = name,   // ← 종목명
            });

            lastValues.Add((name, points.Last().Value ?? 100.0));
        }

        // ── KOSPI 오버레이 (항상 추가, _rawData에 없으면 DB에서 직접 로드) ──
        AddKospiOverlay();

        if (lastValues.Count > 0)
        {
            var best  = lastValues.MaxBy(x => x.last);
            var worst = lastValues.MinBy(x => x.last);
            TxtChartInfo.Text =
                $"▲ {best.label}  {best.last - 100:+0.0;-0.0}%   " +
                $"▼ {worst.label}  {worst.last - 100:+0.0;-0.0}%";
        }

        // Y축 범위를 렌더된 데이터의 min/max에 맞춤
        var yAxis = ChartYAxes[0];
        if (ChartSeries.Count == 0)
        {
            yAxis.MinLimit = null;
            yAxis.MaxLimit = null;
        }
        else
        {
            var allValues = ChartSeries
                .OfType<LineSeries<DateTimePoint>>()
                .SelectMany(s => s.Values ?? [])
                .Select(p => p.Value)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

            if (allValues.Count > 0)
            {
                var dataMin = allValues.Min();
                var dataMax = allValues.Max();
                var margin  = (dataMax - dataMin) * 0.03;  // 3% 여백
                yAxis.MinLimit = Math.Max(0, dataMin - margin);
                yAxis.MaxLimit = dataMax + margin;
            }
        }
    }

    // ═════════════════════════════════════════════════════════
    //  KOSPI 오버레이
    // ═════════════════════════════════════════════════════════

    private void AddKospiOverlay()
    {
        var periodStart = _periodDays == 0
            ? "1900-01-01"
            : DateTime.Today.AddDays(-_periodDays).ToString("yyyy-MM-dd");

        List<(DateTime date, double close)> prices;

        // _rawData에 이미 있으면 재사용, 없으면 DB에서 직접 로드
        if (_rawData.TryGetValue(KospiTicker, out var cached))
        {
            prices = cached.prices;
        }
        else
        {
            try
            {
                var dt = _db.Query(
                    $"SELECT date, adj_close FROM daily_prices " +
                    $"WHERE ticker = '{KospiTicker}' AND date >= '{periodStart}' " +
                    $"ORDER BY date ASC");
                prices = dt.Rows.Cast<DataRow>()
                    .Select(r =>
                    {
                        DateTime.TryParse(r[0]?.ToString(), out var d);
                        double.TryParse(r[1]?.ToString(), out var p);
                        return (date: d, close: p);
                    })
                    .Where(x => x.date != default && x.close > 0)
                    .ToList();
            }
            catch { return; }
        }

        if (prices.Count < 2) return;

        var baseClose = prices.First().close;
        var points = prices
            .Select(d => new DateTimePoint(d.date, Math.Round(d.close / baseClose * 100.0, 4)))
            .ToList();

        // 회색 점선, 얇게 — 배경 기준선 역할
        ChartSeries.Add(new LineSeries<DateTimePoint>
        {
            Values         = new ObservableCollection<DateTimePoint>(points),
            Stroke         = new SolidColorPaint(SKColor.Parse("#585B70"))
                             {
                                 StrokeThickness = 1f,
                                 //PathEffect      = new LiveChartsCore.Drawing.DashEffect([4, 4]),
                             },
            Fill           = null,
            GeometrySize   = 0,
            GeometryFill   = null,
            GeometryStroke = null,
            LineSmoothness = 0,
            Name           = "KOSPI",
        });
    }

    // ═════════════════════════════════════════════════════════
    //  Buttons
    // ═════════════════════════════════════════════════════════

    private void BtnPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        _periodDays = int.Parse(btn.Tag?.ToString() ?? "365");

        foreach (var b in new[] { Btn1M, Btn3M, Btn6M, Btn1Y, Btn2Y, BtnAll })
            b.Foreground = System.Windows.Media.Brushes.Gray;
        btn.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#89B4FA"));

        // 기간 변경 시 DB에서 재로드 (필터는 DB에서 처리)
        if (_currentGroupId >= 0)
            LoadChartData(_currentGroupId);
    }
	private void BtnChart_Click(object sender, RoutedEventArgs e)
    {
        if (GridTicker.ItemsSource is not DataView view) return;
        if (sender is not Button btn) return;

        var checkAll = (btn.Tag?.ToString() ?? "") == "1";
        if (!view.Table.Columns.Contains("chart_selected")) return;

        foreach (DataRow row in view.Table.Rows)
            row["chart_selected"] = checkAll;
    }
	private void BtnDeleteTicker_Click(object sender, RoutedEventArgs e)
	{
		if (_currentGroupId < 0) return;
		if (sender is not Button btn) return;
		if (btn.Tag is not DataRowView row) return;

		var ticker = row["ticker"]?.ToString() ?? "";
		if (string.IsNullOrEmpty(ticker)) return;

		if (MessageBox.Show($"[{ticker}]을 그룹에서 제거하시겠습니까?",
				"제거 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning)
			!= MessageBoxResult.Yes) return;
		try
		{
			using var conn = _db.OpenNativeConnection();
			using var cmd  = conn.CreateCommand();
			cmd.CommandText = "DELETE FROM stock_group_map WHERE group_id = $1 AND ticker = $2";
			cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = _currentGroupId });
			cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
			cmd.ExecuteNonQuery();

			_rawData.Remove(ticker);
			LoadTickers(_currentGroupId);
			ApplyPeriod();
			UpdateGroupCount();
			TxtStatus.Text = $"제거됨: {ticker}";
			StatusChanged?.Invoke($"제거됨: {ticker}", "#F38BA8");
		}
		catch (Exception ex) { TxtStatus.Text = $"제거 오류: {ex.Message}"; }
	}

	// ═════════════════════════════════════════════════════════
	//  그룹 CRUD
	// ═════════════════════════════════════════════════════════

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

	// checkbox 토글 → 즉시 차트 새로고침
	private void ChartSelected_Changed(object sender, RoutedEventArgs e) => ApplyPeriod();

	private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new GroupEditDialog(null);
        if (dlg.ShowDialog() == true) LoadGroups();
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (GridGroup.SelectedItem is not DataRowView row) { TxtStatus.Text = "그룹을 선택하세요."; return; }
        var dlg = new GroupEditDialog(row);
        if (dlg.ShowDialog() == true) LoadGroups();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
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
            StatusChanged?.Invoke($"삭제됨: {name}", "#F38BA8");

            _currentGroupId      = -1;
            TxtGroupName.Text    = "";
            TxtTickerHeader.Text = "TICKERS";
            TxtChartInfo.Text    = "";
            GridTicker.ItemsSource = null;
            ChartSeries.Clear();
            _rawData.Clear();

            LoadGroups();
        }
        catch (Exception ex) { StatusChanged?.Invoke($"삭제 오류: {ex.Message}", "#F38BA8"); }
    }
}
