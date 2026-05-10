using DuckDB.NET.Data;

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
using System.Windows.Input;

namespace Quant.UI.Views;

internal record OhlcvRow(DateTime Date, double Open, double High, double Low, double Close, double AdjClose, long Volume);

public partial class ChartView : UserControl
{
    public event Action<string, string>? StatusChanged;

    public ObservableCollection<ISeries> Series { get; } = [];
    public ObservableCollection<Axis>    XAxes  { get; } = [];
    public ObservableCollection<Axis>    YAxes  { get; } = [];

    public ObservableCollection<ISeries> VolSeries { get; } = [];
    public ObservableCollection<Axis>    VolXAxes  { get; } = [];
    public ObservableCollection<Axis>    VolYAxes  { get; } = [];

    public ObservableCollection<ISeries> MacdSeries { get; } = [];
    public ObservableCollection<Axis>    MacdXAxes  { get; } = [];
    public ObservableCollection<Axis>    MacdYAxes  { get; } = [];

    public ObservableCollection<ISeries> RsiSeries { get; } = [];
    public ObservableCollection<Axis>    RsiXAxes  { get; } = [];
    public ObservableCollection<Axis>    RsiYAxes  { get; } = [];

    // ── X축 스케일 통일의 핵심 ────────────────────────────────
    // 모든 Series(Line/Column/Candle)의 X값 = _epoch.AddDays(i).Ticks
    // UnitWidth = 1일 Ticks 고정 → 차트 간 스케일 동일 → 정렬 완벽
    // SharedWith 불필요: 동일 스케일이면 각 차트가 자동으로 범위 맞춤
    private static readonly DateTime _epoch     = new(2000, 1, 1);
    private static readonly long     _unitTicks = TimeSpan.FromDays(1).Ticks;

    // Labeler 및 Supply 인덱스 역매핑용
    private List<OhlcvRow> _renderData = [];

    private readonly DbManager _db = DbManager.Instance;
    private List<OhlcvRow> _allData = [];
    private int    _periodDays    = 365;
    private string _currentTicker = "";

    private bool _isCandlestick = false;
    private bool _showVol       = true;
    private bool _showMacd      = true;
    private bool _showRsi       = true;
    private readonly HashSet<int> _activeMas = [];

    private static readonly Dictionary<int, SKColor> MaColors = new()
    {
        { 5,   SKColor.Parse("#F9E2AF") },
        { 20,  SKColor.Parse("#A6E3A1") },
        { 60,  SKColor.Parse("#FAB387") },
        { 120, SKColor.Parse("#CBA6F7") },
    };

    public ChartView()
    {
        InitializeComponent();
        DataContext = this;
        InitAxes();
        Loaded += (_, _) => LoadChart();
    }

    // ══════════════════════════════════════════════════════════
    //  Axes 초기화
    //  SharedWith 없음 — 동일 Ticks 스케일로 자동 정렬
    // ══════════════════════════════════════════════════════════
    private void InitAxes()
    {
        XAxes.Add(MakeXAxis(showLabels: true));
        YAxes.Add(MakeYAxis(v => v.ToString("N0")));

        VolXAxes.Add(MakeXAxis(showLabels: false));
        VolYAxes.Add(MakeYAxis(v =>
        {
            if (v >= 1_000_000) return $"{v / 1_000_000:F1}M";
            if (v >= 1_000)     return $"{v / 1_000:F0}K";
            return v.ToString("N0");
        }));

        MacdXAxes.Add(MakeXAxis(showLabels: false));
        MacdYAxes.Add(MakeYAxis(v => v.ToString("F2")));

        RsiXAxes.Add(MakeXAxis(showLabels: false));
        RsiYAxes.Add(MakeYAxis(v => v.ToString("F0"), minLimit: 0, maxLimit: 100));
    }

    // X값: _epoch.AddDays(i).Ticks  UnitWidth: 1일 Ticks
    // Labeler: Ticks → index → _renderData[index].Date 문자열
    private Axis MakeXAxis(bool showLabels = true) => new()
    {
        Labeler = v =>
        {
            var ticks = (long)v;
            if (ticks < _epoch.Ticks) return "";
            var i = (int)Math.Round((ticks - _epoch.Ticks) / (double)_unitTicks);
            if (i < 0 || i >= _renderData.Count) return "";
            var d = _renderData[i].Date;
            return d.Month == 1 && d.Day <= 7
                ? d.ToString("yy/MM")
                : d.ToString("MM/dd");
        },
        UnitWidth       = _unitTicks,
        MinStep         = _unitTicks,
        LabelsPaint     = showLabels ? new SolidColorPaint(SKColor.Parse("#6C7086")) : null,
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
        TextSize        = 9,
    };

    private static Axis MakeYAxis(Func<double, string> labeler,
                                   double? minLimit = null, double? maxLimit = null)
    {
        var ax = new Axis
        {
            Labeler         = labeler,
            LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6C7086")),
            SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
            TextSize        = 9,
            Position        = LiveChartsCore.Measure.AxisPosition.End,
        };
        if (minLimit.HasValue) ax.MinLimit = minLimit;
        if (maxLimit.HasValue) ax.MaxLimit = maxLimit;
        return ax;
    }

    // i → X값(double) 헬퍼
    private static double XT(int i) => (double)(_epoch.AddDays(i).Ticks);

    // ══════════════════════════════════════════════════════════
    //  데이터 로드
    // ══════════════════════════════════════════════════════════
    private void LoadChart()
    {
        var ticker = TxtTickerCode.Text.Trim().ToUpper();
        if (string.IsNullOrEmpty(ticker)) return;
        _currentTicker = ticker;
        try
        {
            RatingCtrl.Rating = _db.GetStockInfo(ticker).rating;
            var (c, r1m, r3m, r1y) = _db.LoadPrice(ticker);
            TxtStockInfo.Text = c > 0
                ? $"현재가 {c:N0}\n  {(r1m >= 0 ? "+" : "")}{r1m:F1}% / {(r3m >= 0 ? "+" : "")}{r3m:F1}% / {(r1y >= 0 ? "+" : "")}{r1y:F1}%"
                : "가격 정보 없음";
            LoadInfoPanel(ticker);

            var sql = $"""
                SELECT date, open, high, low, close, adj_close, volume
                FROM daily_prices WHERE ticker='{ticker}'
                ORDER BY date ASC
                """;
            var dt = _db.Query(sql);

            _allData = dt.Rows.Cast<System.Data.DataRow>()
                .Select(r =>
                {
                    DateTime.TryParse(r[0]?.ToString(), out var d);
                    double.TryParse(r[1]?.ToString(), out var o);
                    double.TryParse(r[2]?.ToString(), out var h);
                    double.TryParse(r[3]?.ToString(), out var l);
                    double.TryParse(r[4]?.ToString(), out var c);
                    double.TryParse(r[5]?.ToString(), out var ac);
                    long.TryParse(r[6]?.ToString(),   out var v);
                    return new OhlcvRow(d, o, h, l, c, ac, v);
                })
                .Where(x => x.Date != default && x.AdjClose > 0)
                .ToList();

            if (_allData.Count == 0)
            {
                TxtChartDataLoadingInfo.Text = "no data";
                StatusChanged?.Invoke($"no data: {ticker}", "#F9E2AF");
                return;
            }

            ApplyPeriod();
            TxtChartDataLoadingInfo.Text =
                $"{_allData.Count:N0}day. {_allData.First().Date:yyyy-MM-dd} ~ {_allData.Last().Date:yyyy-MM-dd}";
            StatusChanged?.Invoke(
                $"{ticker}  {_allData.Count:N0}일  " +
                $"{_allData.First().Date:yyyy-MM-dd} ~ {_allData.Last().Date:yyyy-MM-dd}",
                "#A6E3A1");
        }
        catch (Exception ex) { StatusChanged?.Invoke($"오류: {ex.Message}", "#F38BA8"); }
    }

    // ══════════════════════════════════════════════════════════
    //  ApplyPeriod
    // ══════════════════════════════════════════════════════════
    private void ApplyPeriod()
    {
        var data = _periodDays == 0
            ? _allData
            : _allData.Where(d => d.Date >= DateTime.Today.AddDays(-_periodDays)).ToList();
        if (data.Count == 0) return;

        _renderData = data;   // Labeler가 참조

        RenderMain(data);
        RenderVolume(data);
        RenderMacd(data);
        RenderRsi(data);
        RenderSupply(data);

        var hi  = data.MaxBy(d => d.AdjClose)!;
        var lo  = data.MinBy(d => d.AdjClose)!;
        var ret = (data.Last().AdjClose / data.First().AdjClose - 1) * 100;
        TxtChartPriceInfo.Text = $"{ret:+0.00;-0.00}% (▼ {lo.AdjClose:N0} ~ ▲ {hi.AdjClose:N0})";

        UpdateSubChartVisibility();
    }

    // ══════════════════════════════════════════════════════════
    //  메인 차트
    // ══════════════════════════════════════════════════════════
    private void RenderMain(List<OhlcvRow> data)
    {
        Series.Clear();
        if (_isCandlestick) RenderCandle(data);
        else                RenderLine(data);
        foreach (var p in _activeMas.OrderBy(p => p)) RenderMa(data, p);
    }

    private void RenderLine(List<OhlcvRow> data)
    {
        var pts = data.Select((d, i) => new DateTimePoint(_epoch.AddDays(i), d.AdjClose)).ToList();
        Series.Add(new LineSeries<DateTimePoint>
        {
            Values         = new ObservableCollection<DateTimePoint>(pts),
            Fill           = new SolidColorPaint(SKColor.Parse("#89B4FA").WithAlpha(25)),
            Stroke         = new SolidColorPaint(SKColor.Parse("#89B4FA")) { StrokeThickness = 2 },
            GeometrySize   = 0, GeometryFill = null, GeometryStroke = null,
            LineSmoothness = 0, Name = _currentTicker,
        });
    }

    private void RenderCandle(List<OhlcvRow> data)
    {
        // FinancialPoint(DateTime, high, open, close, low)
        // DateTime = _epoch.AddDays(i) → X스케일이 Line/Volume과 동일
        var pts = data.Select((d, i) =>
        {
            var r = d.Close > 0 ? d.AdjClose / d.Close : 1.0;
            return new FinancialPoint(_epoch.AddDays(i),
                d.High * r, d.Open * r, d.AdjClose, d.Low * r);
        }).ToList();

        Series.Add(new CandlesticksSeries<FinancialPoint>
        {
            Values     = new ObservableCollection<FinancialPoint>(pts),
            UpFill     = new SolidColorPaint(SKColor.Parse("#F38BA8")),
            DownFill   = new SolidColorPaint(SKColor.Parse("#89B4FA")),
            UpStroke   = new SolidColorPaint(SKColor.Parse("#F38BA8")) { StrokeThickness = 1 },
            DownStroke = new SolidColorPaint(SKColor.Parse("#89B4FA")) { StrokeThickness = 1 },
            Name       = _currentTicker,
        });
    }

    private void RenderMa(List<OhlcvRow> data, int period)
    {
        if (data.Count < period) return;
        var prices = data.Select(d => d.AdjClose).ToList();
        var pts    = new List<DateTimePoint>();
        for (int i = period - 1; i < data.Count; i++)
        {
            var avg = prices.Skip(i - period + 1).Take(period).Average();
            pts.Add(new DateTimePoint(_epoch.AddDays(i), avg));
        }
        var color = MaColors[period];
        Series.Add(new LineSeries<DateTimePoint>
        {
            Values         = new ObservableCollection<DateTimePoint>(pts),
            Stroke         = new SolidColorPaint(color) { StrokeThickness = 1 },
            Fill           = null, GeometrySize = 0, GeometryFill = null, GeometryStroke = null,
            LineSmoothness = 0, Name = $"MA{period}",
        });
    }

    // ══════════════════════════════════════════════════════════
    //  거래량
    // ══════════════════════════════════════════════════════════
    private void RenderVolume(List<OhlcvRow> data)
    {
        VolSeries.Clear();
        if (!_showVol) return;

        var pts = data.Select((d, i) => new DateTimePoint(_epoch.AddDays(i), d.Volume)).ToList();
        VolSeries.Add(new ColumnSeries<DateTimePoint>
        {
            Values      = new ObservableCollection<DateTimePoint>(pts),
            Fill        = new SolidColorPaint(SKColor.Parse("#89B4FA").WithAlpha(120)),
            Stroke      = null, MaxBarWidth = 4, Name = "Volume",
        });
    }

    // ══════════════════════════════════════════════════════════
    //  MACD
    // ══════════════════════════════════════════════════════════
    private void RenderMacd(List<OhlcvRow> data)
    {
        MacdSeries.Clear();
        if (!_showMacd || data.Count < 26) return;

        var prices = data.Select(d => d.AdjClose).ToList();
        var ema12  = CalcEma(prices, 12);
        var ema26  = CalcEma(prices, 26);

        var macdVals = new double[data.Count];
        for (int i = 25; i < data.Count; i++)
            macdVals[i] = ema12[i] - ema26[i];

        var sigSlice = CalcEma(macdVals.Skip(25).ToList(), 9);

        var macdLine   = new List<DateTimePoint>();
        var signalLine = new List<DateTimePoint>();
        var histogram  = new List<DateTimePoint>();

        for (int i = 25; i < data.Count; i++)
        {
            var dt  = _epoch.AddDays(i);
            var mv  = macdVals[i];
            var si  = i - 25;
            macdLine.Add(new DateTimePoint(dt, mv));
            if (si >= 8)
            {
                var sv = sigSlice[si];
                signalLine.Add(new DateTimePoint(dt, sv));
                histogram.Add(new DateTimePoint(dt, mv - sv));
            }
        }

        MacdSeries.Add(new LineSeries<DateTimePoint>
        {
            Values         = new ObservableCollection<DateTimePoint>(macdLine),
            Stroke         = new SolidColorPaint(SKColor.Parse("#89B4FA")) { StrokeThickness = 1 },
            Fill           = null, GeometrySize = 0, GeometryFill = null, GeometryStroke = null,
            LineSmoothness = 0, Name = "MACD",
        });
        MacdSeries.Add(new LineSeries<DateTimePoint>
        {
            Values         = new ObservableCollection<DateTimePoint>(signalLine),
            Stroke         = new SolidColorPaint(SKColor.Parse("#F38BA8")) { StrokeThickness = 1 },
            Fill           = null, GeometrySize = 0, GeometryFill = null, GeometryStroke = null,
            LineSmoothness = 0, Name = "Signal",
        });
        MacdSeries.Add(new ColumnSeries<DateTimePoint>
        {
            Values      = new ObservableCollection<DateTimePoint>(histogram),
            Fill        = new SolidColorPaint(SKColor.Parse("#A6E3A1").WithAlpha(160)),
            Stroke      = null, MaxBarWidth = 4, Name = "Histogram",
        });
    }

    // ══════════════════════════════════════════════════════════
    //  RSI
    // ══════════════════════════════════════════════════════════
    private void RenderRsi(List<OhlcvRow> data)
    {
        RsiSeries.Clear();
        if (!_showRsi || data.Count < 15) return;

        var prices = data.Select(d => d.AdjClose).ToList();
        var pts    = new List<DateTimePoint>();

        double avgGain = 0, avgLoss = 0;
        for (int i = 1; i <= 14; i++)
        {
            var diff = prices[i] - prices[i - 1];
            if (diff > 0) avgGain += diff; else avgLoss += -diff;
        }
        avgGain /= 14; avgLoss /= 14;

        for (int i = 15; i < prices.Count; i++)
        {
            var diff = prices[i] - prices[i - 1];
            avgGain = (avgGain * 13 + (diff > 0 ? diff : 0)) / 14;
            avgLoss = (avgLoss * 13 + (diff < 0 ? -diff : 0)) / 14;
            var rsi = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
            pts.Add(new DateTimePoint(_epoch.AddDays(i), rsi));
        }

        RsiSeries.Add(new LineSeries<DateTimePoint>
        {
            Values         = new ObservableCollection<DateTimePoint>(pts),
            Stroke         = new SolidColorPaint(SKColor.Parse("#CBA6F7")) { StrokeThickness = 1 },
            Fill           = null, GeometrySize = 0, GeometryFill = null, GeometryStroke = null,
            LineSmoothness = 0, Name = "RSI(14)",
        });
    }

    // ══════════════════════════════════════════════════════════
    //  수급 오버레이 (supply 데이터 준비 시 활성화)
    // ══════════════════════════════════════════════════════════
    private void RenderSupply(List<OhlcvRow> data)
    {
        var toRemove = VolSeries.Where(s => s.Name is "외인순매수" or "기관순매수").ToList();
        foreach (var s in toRemove) VolSeries.Remove(s);
        if (data.Count == 0) return;

        var dateToIdx = data.Select((r, i) => (r.Date.Date, i))
                            .ToDictionary(x => x.Date, x => x.i);
        var sql = $"""
            SELECT date, inst_net_buy, foreign_net_buy
            FROM supply WHERE ticker='{_currentTicker}'
              AND date >= '{data.First().Date:yyyy-MM-dd}'
              AND date <= '{data.Last().Date:yyyy-MM-dd}'
            ORDER BY date ASC
            """;
        try
        {
            var dt  = _db.Query(sql);
            if (dt.Rows.Count == 0) return;

            var fgnPts  = new List<DateTimePoint>();
            var instPts = new List<DateTimePoint>();

            foreach (System.Data.DataRow r in dt.Rows)
            {
                if (!DateTime.TryParse(r[0]?.ToString(), out var d)) continue;
                if (!dateToIdx.TryGetValue(d.Date, out var idx))    continue;
                long.TryParse(r[1]?.ToString(), out var inst);
                long.TryParse(r[2]?.ToString(), out var fgn);
                if (inst != 0) instPts.Add(new DateTimePoint(_epoch.AddDays(idx), inst));
                if (fgn  != 0) fgnPts.Add(new DateTimePoint(_epoch.AddDays(idx), fgn));
            }

            if (fgnPts.Count > 0)
                VolSeries.Add(new LineSeries<DateTimePoint>
                {
                    Values         = new ObservableCollection<DateTimePoint>(fgnPts),
                    Stroke         = new SolidColorPaint(SKColor.Parse("#F38BA8")) { StrokeThickness = 1 },
                    Fill           = null, GeometrySize = 0, GeometryFill = null, GeometryStroke = null,
                    LineSmoothness = 0, Name = "외인순매수", ScalesYAt = 1,
                });
            if (instPts.Count > 0)
                VolSeries.Add(new LineSeries<DateTimePoint>
                {
                    Values         = new ObservableCollection<DateTimePoint>(instPts),
                    Stroke         = new SolidColorPaint(SKColor.Parse("#A6E3A1")) { StrokeThickness = 1 },
                    Fill           = null, GeometrySize = 0, GeometryFill = null, GeometryStroke = null,
                    LineSmoothness = 0, Name = "기관순매수", ScalesYAt = 1,
                });

            if (VolYAxes.Count < 2)
                VolYAxes.Add(MakeYAxis(v =>
                {
                    var m = v / 10_000.0;
                    return Math.Abs(m) >= 1 ? $"{m:F0}만" : v.ToString("N0");
                }));
        }
        catch { /* supply 미준비 시 무시 */ }
    }

    // ══════════════════════════════════════════════════════════
    //  EMA
    // ══════════════════════════════════════════════════════════
    private static List<double> CalcEma(List<double> prices, int period)
    {
        var result = new List<double>(prices.Count);
        var k      = 2.0 / (period + 1);
        double ema = prices.Take(period).Average();
        for (int i = 0; i < period - 1; i++) result.Add(0);
        result.Add(ema);
        for (int i = period; i < prices.Count; i++)
        {
            ema = prices[i] * k + ema * (1 - k);
            result.Add(ema);
        }
        return result;
    }

    // ══════════════════════════════════════════════════════════
    //  서브차트 가시성
    // ══════════════════════════════════════════════════════════
    private void UpdateSubChartVisibility()
    {
        ChartGrid.RowDefinitions[4].Height = _showVol  ? new GridLength(100) : new GridLength(0);
        ChartGrid.RowDefinitions[5].Height = _showMacd ? new GridLength(100) : new GridLength(0);
        ChartGrid.RowDefinitions[6].Height = _showRsi  ? new GridLength(100) : new GridLength(0);
    }

    // ══════════════════════════════════════════════════════════
    //  버튼 핸들러
    // ══════════════════════════════════════════════════════════
    private void BtnChartType_Click(object sender, RoutedEventArgs e)
    {
        _isCandlestick = !_isCandlestick;
        BtnChartType.Content = _isCandlestick ? "봉" : "선";
        ApplyPeriod();
    }

    private void BtnLog_Click(object sender, RoutedEventArgs e) { }

    private void BtnMa_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var period = int.Parse(btn.Tag?.ToString() ?? "0");
        if (_activeMas.Contains(period)) _activeMas.Remove(period);
        else                             _activeMas.Add(period);
        ApplyPeriod();
    }

    private void BtnVol_Click(object sender, RoutedEventArgs e)  { _showVol  = !_showVol;  ApplyPeriod(); }
    private void BtnMacd_Click(object sender, RoutedEventArgs e) { _showMacd = !_showMacd; ApplyPeriod(); }
    private void BtnRsi_Click(object sender, RoutedEventArgs e)  { _showRsi  = !_showRsi;  ApplyPeriod(); }

    private static void HighlightToggle(Button btn, bool active) { }

    // ══════════════════════════════════════════════════════════
    //  Info 패널
    // ══════════════════════════════════════════════════════════
    private void LoadInfoPanel(string ticker)
    {
        try
        {
            var sql = $"""
                SELECT date, inst_net_buy, foreign_net_buy, inst_net_amount, foreign_net_amount
                FROM supply WHERE ticker='{ticker}'
                ORDER BY date DESC LIMIT 1
                """;
            var dt = _db.Query(sql);
            if (dt.Rows.Count > 0)
            {
                var r = dt.Rows[0];
                long.TryParse(r[1]?.ToString(), out var instBuy);
                long.TryParse(r[2]?.ToString(), out var fgnBuy);
                long.TryParse(r[3]?.ToString(), out var instAmt);
                long.TryParse(r[4]?.ToString(), out var fgnAmt);
                TxtInstNet.Text    = FormatNet(instBuy);
                TxtForeignNet.Text = FormatNet(fgnBuy);
                TxtInstAmt.Text    = FormatAmt(instAmt);
                TxtForeignAmt.Text = FormatAmt(fgnAmt);
                TxtSupplyDate.Text = $" ({r[0]})";
                TxtInstNet.Foreground    = BrushForNet(instBuy);
                TxtForeignNet.Foreground = BrushForNet(fgnBuy);
                TxtInstAmt.Foreground    = BrushForNet(instAmt);
                TxtForeignAmt.Foreground = BrushForNet(fgnAmt);
            }
            else
            {
                TxtInstNet.Text = TxtForeignNet.Text = TxtInstAmt.Text = TxtForeignAmt.Text = "N/A";
                TxtSupplyDate.Text = "";
            }
        }
        catch { TxtInstNet.Text = TxtForeignNet.Text = "ERR"; }

        try
        {
            var sql = $"""
                SELECT report_date, per, pbr, eps, roe
                FROM fundamentals WHERE ticker='{ticker}'
                ORDER BY report_date DESC LIMIT 1
                """;
            var dt = _db.Query(sql);
            if (dt.Rows.Count > 0)
            {
                var r = dt.Rows[0];
                double.TryParse(r[1]?.ToString(), out var per);
                double.TryParse(r[2]?.ToString(), out var pbr);
                double.TryParse(r[3]?.ToString(), out var eps);
                double.TryParse(r[4]?.ToString(), out var roe);
                TxtPer.Text = per == 0 ? "-" : per.ToString("F1");
                TxtPbr.Text = pbr == 0 ? "-" : pbr.ToString("F2");
                TxtEps.Text = eps == 0 ? "-" : eps.ToString("N0");
                TxtBps.Text = roe == 0 ? "-" : roe.ToString("F1") + "%";
                TxtFundDate.Text = $" ({r[0]})";
            }
            else { TxtPer.Text = TxtPbr.Text = TxtEps.Text = TxtBps.Text = "N/A"; TxtFundDate.Text = ""; }
        }
        catch { TxtPer.Text = TxtPbr.Text = "ERR"; }

        try
        {
            var sql = $"""
                SELECT date, title, writer, filepath
                FROM pdf_reports WHERE ticker='{TxtStockName.Text}'
                ORDER BY date DESC LIMIT 5000
                """;
            GridReport.ItemsSource = _db.Query(sql).DefaultView;
        }
        catch { GridReport.ItemsSource = null; }
    }

    private static string FormatNet(long v) => v == 0 ? "0" : v > 0 ? $"+{v:N0}" : $"{v:N0}";
    private static string FormatAmt(long v)
    {
        var e = v / 100_000_000.0;
        if (Math.Abs(e) >= 1) return e > 0 ? $"+{e:F0}" : $"{e:F0}";
        var m = v / 10_000.0;
        return m > 0 ? $"+{m:F0}만" : $"{m:F0}만";
    }
    private static System.Windows.Media.Brush BrushForNet(long v) =>
        v >= 0
            ? new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#89B4FA"))
            : new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F38BA8"));

    // ══════════════════════════════════════════════════════════
    //  외부 호출
    // ══════════════════════════════════════════════════════════
    public void LoadTicker(string ticker, string name)
    {
        TxtTickerCode.Text = ticker.ToUpper();
        TxtStockName.Text  = name;
        LoadChart();
    }

    private void BtnLoad_Click(object sender, RoutedEventArgs e) => LoadChart();
    private void TxtTickerCode_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Return) LoadChart(); }

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

    private void BtnExternalLink_Click(object sender, RoutedEventArgs e) =>
        Helpers.OpenExternalLink(TxtTickerCode.Text);

    private async void BtnDownloadData_Click(object sender, RoutedEventArgs e)
    {
        var ticker = TxtTickerCode.Text.Trim().ToUpper();
        if (string.IsNullOrEmpty(ticker)) return;
        if (sender is Button btn) btn.IsEnabled = false;
        try
        {
            var svc      = new PriceDownloadService(_db);
            var progress = new Progress<string>(msg => StatusChanged?.Invoke(msg, "#89B4FA"));
            var (inserted, lastDate) = await svc.DownloadAsync(ticker, progress);
            LoadChart();
            var msg = inserted > 0
                ? $"{ticker}: {inserted:N0}건 저장 완료 (최신 {lastDate})"
                : $"{ticker}: 신규 데이터 없음 (최신 {lastDate})";
            StatusChanged?.Invoke(msg, "#A6E3A1");
        }
        catch (Exception ex) { StatusChanged?.Invoke($"다운로드 오류: {ex.Message}", "#F38BA8"); }
        finally { if (sender is Button btn2) btn2.IsEnabled = true; }
    }

    private void RatingCtrl_RatingChanged(int rating)
    {
        if (string.IsNullOrEmpty(_currentTicker)) return;
        try
        {
            _db.SetStockRating(rating, _currentTicker);
            StatusChanged?.Invoke($"{_currentTicker} rating 저장: {rating}", "#A6E3A1");
        }
        catch (Exception ex) { StatusChanged?.Invoke($"rating 저장 오류: {ex.Message}", "#F38BA8"); }
    }

    private void GridReportClick(object sender, SelectionChangedEventArgs e)
    {
        if (GridReport.SelectedItem is not DataRowView row) return;
        var filepath = row["filepath"]?.ToString();
        var (ok, message) = Helpers.OpenWithChrome(filepath);
        TxtStatus.Text = message;
        StatusChanged?.Invoke(message, ok ? "#89B4FA" : "#F38BA8");
    }
}
