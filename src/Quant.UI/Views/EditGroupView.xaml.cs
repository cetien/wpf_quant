using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Quant.Core.Infrastructure;
using Quant.Core.Services;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Data;
using System.Windows;
using System.Windows.Controls;

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
                "SELECT group_id, kind, name, description, rating, is_active " +
                "FROM groups ORDER BY kind, name");
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
                $"SELECT m.ticker, s.name, s.market, m.weight, m.created_at " +
                $"FROM stock_group_map m " +
                $"JOIN stocks s ON s.ticker = m.ticker " +
                $"WHERE m.group_id = {groupId} " +
                $"ORDER BY m.ticker");
            GridTicker.ItemsSource = dt.DefaultView;
        }
        catch (Exception ex) { TxtStatus.Text = $"티커 로드 오류: {ex.Message}"; }
    }

    private void GridTicker_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void BtnAddTicker_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGroupId < 0) { TxtStatus.Text = "그룹을 먼저 선택하세요."; return; }
        TxtStatus.Text = "티커 추가 — 구현 예정";
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

    private void LoadChartData(int groupId)
    {
        try
        {
            // ticker + name 함께 조회
            var memberDt = _db.Query(
                $"SELECT m.ticker, s.name FROM stock_group_map m " +
                $"JOIN stocks s ON s.ticker = m.ticker " +
                $"WHERE m.group_id = {groupId} ORDER BY m.ticker");

            if (memberDt.Rows.Count == 0)
            {
                ChartSeries.Clear();
                _rawData.Clear();
                TxtChartInfo.Text = "종목 없음";
                TxtStatus.Text    = "그룹에 속한 종목이 없습니다.";
                return;
            }

            _rawData.Clear();
            foreach (DataRow r in memberDt.Rows)
            {
                var ticker = r["ticker"].ToString()!;
                var name   = r["name"].ToString()!;

                var priceDt = _db.Query(
                    $"SELECT date, adj_close FROM daily_prices " +
                    $"WHERE ticker = '{ticker}' ORDER BY date ASC");

                var prices = priceDt.Rows.Cast<DataRow>()
                    .Select(pr =>
                    {
                        DateTime.TryParse(pr[0]?.ToString(), out var d);
                        double.TryParse(pr[1]?.ToString(), out var p);
                        return (date: d, close: p);
                    })
                    .Where(x => x.date != default && x.close > 0)
                    .ToList();

                if (prices.Count > 0)
                    _rawData[ticker] = (name, prices);
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

    private void ApplyPeriod()
    {
        ChartSeries.Clear();
        if (_rawData.Count == 0) return;

        var periodStart = _periodDays == 0
            ? DateTime.MinValue
            : DateTime.Today.AddDays(-_periodDays);

        int colorIdx = 0;
        var lastValues = new List<(string label, double last)>();

        foreach (var (ticker, (name, allPrices)) in _rawData)
        {
            var filtered = _periodDays == 0
                ? allPrices
                : allPrices.Where(d => d.date >= periodStart).ToList();

            if (filtered.Count < 2) continue;

            var baseClose = filtered.First().close;
            var points = filtered
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

        if (lastValues.Count > 0)
        {
            var best  = lastValues.MaxBy(x => x.last);
            var worst = lastValues.MinBy(x => x.last);
            TxtChartInfo.Text =
                $"▲ {best.label}  {best.last - 100:+0.0;-0.0}%   " +
                $"▼ {worst.label}  {worst.last - 100:+0.0;-0.0}%";
        }
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

        ApplyPeriod();
    }
	private void BtnChart_Click(object sender, RoutedEventArgs e)
    {

    }
	private void BtnDeleteTicker_Click(object sender, RoutedEventArgs e)
	{

	}

	// ═════════════════════════════════════════════════════════
	//  그룹 CRUD
	// ═════════════════════════════════════════════════════════

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
