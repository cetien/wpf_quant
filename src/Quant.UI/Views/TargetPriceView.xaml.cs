// TargetPriceView.xaml.cs
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

using Quant.Core.Infrastructure;
using Quant.Core.Models;

using SkiaSharp;

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Quant.UI.Views;

// target_price_stocks 행 (최신 1행)
public record TpStockRow
{
    public string Ticker { get; init; } = "";
    public string? Name { get; init; }
    public string? Date { get; init; }
    public int? ReportCount { get; init; }
    public int? AvgTgt { get; init; }
    public int? MinTgt { get; init; }
    public int? MaxTgt { get; init; }
    public int? CurPrice { get; init; }
    public float? Upside { get; init; }
}

// target_price_monthly 행
public record TpMonthlyRow
{
    public string? Ticker { get; init; }
    public string? Ym { get; init; }
    public int? ReportCount { get; init; }
    public int? AvgTgt { get; init; }
    public int? MinTgt { get; init; }
    public int? MaxTgt { get; init; }
    public int? Price { get; init; }
    public float? Upside { get; init; }
}

public partial class TargetPriceView : UserControl
{
    // ── LiveCharts 바인딩 속성 ────────────────────────────────
    public ObservableCollection<ISeries> TpSeries    { get; } = [];
    public ObservableCollection<Axis>    TpXAxes     { get; } = [];
    public ObservableCollection<Axis>    TpYAxes     { get; } = [];

    public ObservableCollection<ISeries> UpsideSeries { get; } = [];
    public ObservableCollection<Axis>    UpsideXAxes  { get; } = [];
    public ObservableCollection<Axis>    UpsideYAxes  { get; } = [];

    // ── 내부 상태 ─────────────────────────────────────────────
    private readonly DbManager _db;
    private string             _currentTicker = "";
    private bool               _isChartMode   = true;   // true=Chart, false=Grid
    private bool               _isInternalTextChange = false;

    // X축 레이블용 월 목록 (인덱스 → ym)
    private List<string> _ymLabels = [];

    public TargetPriceView(DbManager db)
    {
        _db = db;
        InitializeComponent();
        DataContext = this;

        InitAxes();
        HighlightTab(_isChartMode);
    }

    // ══════════════════════════════════════════════════════════
    //  Axes 초기화
    // ══════════════════════════════════════════════════════════
    private void InitAxes()
    {
        // 메인 차트 X축 (레이블 표시)
        TpXAxes.Add(new Axis
        {
            Labeler           = i => _ymLabels.Count > 0 && i >= 0 && i < _ymLabels.Count
                                    ? _ymLabels[(int)i]
                                    : "",
            UnitWidth         = 1,
            MinStep           = 1,
            LabelsPaint       = new SolidColorPaint(SKColor.Parse("#6C7086")),
            SeparatorsPaint   = new SolidColorPaint(SKColor.Parse("#313244")),
            TextSize          = 9,
        });
        TpYAxes.Add(new Axis
        {
            Labeler         = v => v.ToString("N0"),
            LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6C7086")),
            SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
            TextSize        = 9,
            Position        = LiveChartsCore.Measure.AxisPosition.End,
        });

        // 서브 차트 (upside) X축 — 레이블 없음, 공유 스케일
        UpsideXAxes.Add(new Axis
        {
            Labeler           = _ => "",
            UnitWidth         = 1,
            MinStep           = 1,
            LabelsPaint       = null,
            SeparatorsPaint   = new SolidColorPaint(SKColor.Parse("#313244")),
        });
        UpsideYAxes.Add(new Axis
        {
            Labeler         = v => $"{v:P0}",
            LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6C7086")),
            SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
            TextSize        = 9,
            Position        = LiveChartsCore.Measure.AxisPosition.End,
        });
    }

    // ══════════════════════════════════════════════════════════
    //  종목 로드
    // ══════════════════════════════════════════════════════════
    public void LoadTicker(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return;
        ticker = ticker.Trim().ToUpper();
        _currentTicker = ticker;

        _isInternalTextChange = true;
        TxtStockSearch.Text   = ticker;
        _isInternalTextChange = false;

        SetStatus($"로딩: {ticker}...");
        try
        {
            // 1. 데이터 쿼리 실행
            var stockRow = _db.QueryFirst<TpStockRow>("""
                SELECT ticker, name, CAST(date AS VARCHAR) AS Date,
                       report_count AS ReportCount,
                       avg_tgt      AS AvgTgt,
                       min_tgt      AS MinTgt,
                       max_tgt      AS MaxTgt,
                       cur_price    AS CurPrice,
                       upside       AS Upside
                FROM   target_price_stocks
                WHERE  ticker = $ticker
                ORDER  BY date DESC
                LIMIT  1
                """, new { ticker });

            var monthlyRows = _db.Query<TpMonthlyRow>("""
                SELECT ticker, ym,
                       report_count AS ReportCount,
                       avg_tgt      AS AvgTgt,
                       min_tgt      AS MinTgt,
                       max_tgt      AS MaxTgt,
                       price        AS Price,
                       upside       AS Upside
                FROM   target_price_monthly
                WHERE  ticker = $ticker
                ORDER  BY ym ASC
                """, new { ticker }).ToList();

            // 2. 함수로 결과 전달
            LoadStockPanel(stockRow, monthlyRows.LastOrDefault());
            LoadMonthlyData(monthlyRows);

            SetStatus($"{ticker}  •  {_ymLabels.Count}개월");
        }
        catch (Exception ex)
        {
            SetStatus($"오류: {ex.Message}");
        }
    }

    // ── 상단 패널: target_price_stocks 최신 row ───────────────
    private void LoadStockPanel(TpStockRow? row, TpMonthlyRow? latestMonthly)
    {
        PanelStockInfo.Children.Clear();

        if (row is null)
        {
            AddChip(PanelStockInfo, "데이터 없음", "#45475A", "#6C7086");
            return;
        }

        // 종목명
        if (!string.IsNullOrEmpty(row.Name))
            AddChip(PanelStockInfo, row.Name, "#313244", "#CDD6F4", bold: true);

        // 기준일
        if (!string.IsNullOrEmpty(row.Date))
            AddChip(PanelStockInfo, $"📅 {row.Date}", "#313244", "#6C7086");

        // 리포트 수
        if (row.ReportCount.HasValue)
            AddChip(PanelStockInfo, $"📄 {row.ReportCount}건", "#313244", "#89B4FA");

        // 평균 목표주가
        var avgTgt = latestMonthly?.AvgTgt ?? row.AvgTgt;
        if (avgTgt.HasValue)
            AddChip(PanelStockInfo, $"⊕ avg {avgTgt.Value:N0}", "#1A2B3C", "#89B4FA", bold: true);

        // 범위
        var minTgt = latestMonthly?.MinTgt ?? row.MinTgt;
        var maxTgt = latestMonthly?.MaxTgt ?? row.MaxTgt;
        if (minTgt.HasValue && maxTgt.HasValue)
            AddChip(PanelStockInfo, $"▾{minTgt.Value:N0} ~ ▴{maxTgt.Value:N0}", "#313244", "#6C7086");

        // 현재가
        if (row.CurPrice.HasValue)
            AddChip(PanelStockInfo, $"현재 {row.CurPrice.Value:N0}", "#313244", "#A6E3A1");

        // Upside (평균 목표가와 현재가로 재계산)
        if (avgTgt.HasValue && row.CurPrice.HasValue && row.CurPrice.Value > 0)
        {
            double calculatedUpside = ((double)avgTgt.Value / row.CurPrice.Value) - 1;
            var upsideColor = calculatedUpside >= 0 ? "#A6E3A1" : "#F38BA8";
            AddChip(PanelStockInfo, $"Upside {calculatedUpside:P1}", "#1A1A2E", upsideColor, bold: true);
        }
    }

    // ── 월별 데이터 로드 + 차트/그리드 렌더링 ─────────────────
    private void LoadMonthlyData(List<TpMonthlyRow> rows)
    {
        RenderChart(rows);
        RenderGrid(rows);
    }

    // ── Chart 렌더링 ──────────────────────────────────────────
    private void RenderChart(List<TpMonthlyRow> rows)
    {
        TpSeries.Clear();
        UpsideSeries.Clear();
        _ymLabels.Clear();

        if (rows.Count == 0) return;

        _ymLabels.AddRange(rows.Select(r => r.Ym ?? ""));

        var avgTgtPts = new List<ObservablePoint>();
        var pricePts  = new List<ObservablePoint>();
        var minTgtPts = new List<ObservablePoint>();
        var maxTgtPts = new List<ObservablePoint>();
        var upsidePts = new List<ObservablePoint>();

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.AvgTgt.HasValue)  avgTgtPts.Add(new ObservablePoint(i, r.AvgTgt.Value));
            if (r.Price.HasValue)   pricePts.Add(new ObservablePoint(i, r.Price.Value));
            if (r.MinTgt.HasValue)  minTgtPts.Add(new ObservablePoint(i, r.MinTgt.Value));
            if (r.MaxTgt.HasValue)  maxTgtPts.Add(new ObservablePoint(i, r.MaxTgt.Value));
            if (r.Upside.HasValue)  upsidePts.Add(new ObservablePoint(i, (double)r.Upside.Value));
        }

        // Max TP (점선)
        TpSeries.Add(new LineSeries<ObservablePoint>
        {
            Name              = "Max TP",
            Values            = maxTgtPts,
            Stroke            = new SolidColorPaint(SKColor.Parse("#89B4FA"), 1) 
                                { PathEffect = new LiveChartsCore.SkiaSharpView.Painting.Effects.DashEffect(new float[] { 4, 4 }) },
            Fill              = null,
            GeometryFill      = null,
            GeometryStroke    = null,
            GeometrySize      = 0,
            LineSmoothness    = 0,
        });

        // Min TP (점선)
        TpSeries.Add(new LineSeries<ObservablePoint>
        {
            Name              = "Min TP",
            Values            = minTgtPts,
            Stroke            = new SolidColorPaint(SKColor.Parse("#89B4FA"), 1) 
                                { PathEffect = new LiveChartsCore.SkiaSharpView.Painting.Effects.DashEffect(new float[] { 4, 4 }) },
            Fill              = null,
            GeometryFill      = null,
            GeometryStroke    = null,
            GeometrySize      = 0,
            LineSmoothness    = 0,
        });

        // avg_tgt 파란 선
        TpSeries.Add(new LineSeries<ObservablePoint>
        {
            Name              = "Avg TP",
            Values            = avgTgtPts,
            Stroke            = new SolidColorPaint(SKColor.Parse("#89B4FA"), 2),
            Fill              = null,
            GeometryFill      = new SolidColorPaint(SKColor.Parse("#89B4FA")),
            GeometryStroke    = null,
            GeometrySize      = 4,
            LineSmoothness    = 0,
        });

        // price 초록 선
        TpSeries.Add(new LineSeries<ObservablePoint>
        {
            Name              = "Price",
            Values            = pricePts,
            Stroke            = new SolidColorPaint(SKColor.Parse("#A6E3A1"), 2),
            Fill              = null,
            GeometryFill      = new SolidColorPaint(SKColor.Parse("#A6E3A1")),
            GeometryStroke    = null,
            GeometrySize      = 4,
            LineSmoothness    = 0,
        });

        // upside % 막대 (서브차트)
        UpsideSeries.Add(new ColumnSeries<ObservablePoint>
        {
            Name              = "Upside %",
            Values            = upsidePts,
            Fill              = new SolidColorPaint(SKColor.Parse("#CBA6F7")),
            Stroke            = null,
        });
    }

    // ── Grid 렌더링 ───────────────────────────────────────────
    private void RenderGrid(List<TpMonthlyRow> rows)
    {
        GridMonthly.ItemsSource = rows;
    }

    // ══════════════════════════════════════════════════════════
    //  탭 전환
    // ══════════════════════════════════════════════════════════
    private void BtnToggleChart_Click(object sender, RoutedEventArgs e)
    {
        _isChartMode = true;
        PanelChart.Visibility    = Visibility.Visible;
        GridMonthly.Visibility   = Visibility.Collapsed;
        HighlightTab(true);
    }

    private void BtnToggleGrid_Click(object sender, RoutedEventArgs e)
    {
        _isChartMode = false;
        PanelChart.Visibility    = Visibility.Collapsed;
        GridMonthly.Visibility   = Visibility.Visible;
        HighlightTab(false);
    }

    private void HighlightTab(bool chartActive)
    {
        var activeColor   = (Color)ColorConverter.ConvertFromString("#89B4FA");
        var inactiveColor = (Color)ColorConverter.ConvertFromString("#6C7086");
        var activeBg      = (Color)ColorConverter.ConvertFromString("#313244");
        var inactiveBg    = (Color)ColorConverter.ConvertFromString("#252536");

        BtnToggleChart.Background = new SolidColorBrush(chartActive  ? activeBg : inactiveBg);
        BtnToggleChart.Foreground = new SolidColorBrush(chartActive  ? activeColor : inactiveColor);

        BtnToggleGrid.Background  = new SolidColorBrush(!chartActive ? activeBg : inactiveBg);
        BtnToggleGrid.Foreground  = new SolidColorBrush(!chartActive ? activeColor : inactiveColor);
    }

    // ══════════════════════════════════════════════════════════
    //  종목 검색 (ChartView 동일 패턴)
    // ══════════════════════════════════════════════════════════
    private void TxtStockSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInternalTextChange) return;

        string keyword = TxtStockSearch.Text.Trim();
        if (keyword.Length < 1)
        {
            PopupSearch.IsOpen = false;
            return;
        }

        var list = SearchStocks(keyword);
        ListSearchResult.ItemsSource = list;
        PopupSearch.IsOpen = list.Count > 0;
    }

    private void TxtStockSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (ListSearchResult.Items.Count > 0)
                {
                    ListSearchResult.Focus();
                    ListSearchResult.SelectedIndex = 0;
                }
                e.Handled = true;
                break;

            case Key.Enter:
                if (ListSearchResult.SelectedItem is StockItem)
                    SelectStock();
                else
                {
                    LoadTicker(TxtStockSearch.Text);
                    PopupSearch.IsOpen = false;
                }
                e.Handled = true;
                break;
        }
    }

    private void ListSearchResult_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => SelectStock();

    private void ListSearchResult_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SelectStock();
    }

    private void SelectStock()
    {
        if (ListSearchResult.SelectedItem is not StockItem item) return;
        PopupSearch.IsOpen = false;
        LoadTicker(item.Ticker);
    }

    private List<StockItem> SearchStocks(string keyword)
    {
        keyword = keyword.Trim();
        return _db.StockItemList()
            .Select(x => new
            {
                Item  = x,
                Score =
                    x.Ticker.Equals(keyword, StringComparison.OrdinalIgnoreCase)        ? 100 :
                    x.Name.Equals(keyword, StringComparison.OrdinalIgnoreCase)          ? 90  :
                    x.Ticker.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)    ? 80  :
                    x.Name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)      ? 70  :
                    x.Ticker.Contains(keyword, StringComparison.OrdinalIgnoreCase)      ? 60  :
                    x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)        ? 50  :
                    0
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Name)
            .Take(30)
            .Select(x => x.Item)
            .ToList();
    }

    // ══════════════════════════════════════════════════════════
    //  기타 이벤트
    // ══════════════════════════════════════════════════════════
    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentTicker))
            LoadTicker(_currentTicker);
    }

    private void TextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
    }

    // ══════════════════════════════════════════════════════════
    //  상태 텍스트
    // ══════════════════════════════════════════════════════════
    private void SetStatus(string text) => TxtStatus.Text = text;

    // ══════════════════════════════════════════════════════════
    //  Chip UI 헬퍼
    // ══════════════════════════════════════════════════════════
    private static void AddChip(WrapPanel panel, string text, string bg, string fg,
                                  bool bold = false)
    {
        var border = new Border
        {
            Background    = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg)),
            CornerRadius  = new CornerRadius(4),
            Padding       = new Thickness(6, 2, 6, 2),
            Margin        = new Thickness(0, 0, 6, 4),
        };
        var tb = new TextBlock
        {
            Text            = text,
            Foreground      = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg)),
            FontFamily      = new FontFamily("Consolas"),
            FontSize        = 11,
            FontWeight      = bold ? FontWeights.Bold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        border.Child = tb;
        panel.Children.Add(border);
    }
}
