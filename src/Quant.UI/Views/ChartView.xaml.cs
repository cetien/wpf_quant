using DuckDB.NET.Data;

using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

using Quant.Core.Infrastructure;
using Quant.Core.Services;
using Quant.UI; // Helpers 및 LabelValue 사용을 위해 추가

using SkiaSharp;

using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using Quant.Core.Models;

namespace Quant.UI.Views;

internal record OhlcvRow(DateTime Date, double Open, double High, double Low, double Close, double AdjClose, long Volume);

// 신규종목 등록 팝업 상태
internal record RegisterState(string Ticker, string? FetchedName, string? FetchedMarket, string? FetchedType);

public class GroupKindEmojiConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Helpers.GroupKind2Emoji(value?.ToString() ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

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

    // ── X축 스케일 통일 ────────────────────────────────────────
    // 모든 Series X값 = 순수 정수 인덱스 (0, 1, 2, ...)
    // UnitWidth = 1, Labeler: index → _renderData[i].Date
    // DateTimePoint(DateTime) 대신 ObservablePoint(int, double) 사용
    // → MACD/RSI 워밍업 오프셋과 무관하게 동일 index 기준으로 정렬 보장

    public ObservableCollection<LabelValue> FundamentalMetrics { get; } = [];
    public ObservableCollection<LabelValue> SupplyMetrics      { get; } = [];

    // Labeler 및 Supply 인덱스 역매핑용
    private List<OhlcvRow> _renderData = [];

    // ── 툴팁용 캐시 ──────────────────────────────────────────
    // RenderSupply 시점에 index → (inst, fgn) 매핑 저장
    // MouseMove에서 O(1) 조회
    private Dictionary<int, (long Inst, long Fgn)> _supplyCache = [];

    // RenderMacd 결과 캐시: index → (macd, signal, hist)
    private Dictionary<int, (double Macd, double Signal, double Hist)> _macdCache = [];

    // RenderRsi 결과 캐시: index → rsi
    private Dictionary<int, double> _rsiCache = [];

    private readonly DbManager _db;
    private List<OhlcvRow> _allData = [];
    private int    _periodDays    = 365;
    private string _currentTicker = "";
    private StockCache _currentCache = new();

    private InfoPanelBuilder? infoPanel = null;
    private InfoPanelBuilder? infoPanel2 = null;

    // 신규종목 등록 팝업 상태
    private RegisterState? _pendingRegister;

    private bool _isCandlestick = false;
    private bool _showVol       = true;
    private bool _showMacd      = true;
    private bool _showRsi       = true;
    private readonly HashSet<int> _activeMas = [20, 60, 120];

    private static readonly Dictionary<int, SKColor> MaColors = new()
    {
        { 5,   SKColor.Parse("#F9E2AF") },
        { 20,  SKColor.Parse("#A6E3A1") },
        { 60,  SKColor.Parse("#FAB387") },
        { 120, SKColor.Parse("#CBA6F7") },
    };

    public ChartView(DbManager db)
    {
        _db = db;
        InitializeComponent();
        DataContext = this;
        InitAxes();
        Loaded += (_, _) =>
        {
            infoPanel = new InfoPanelBuilder(Panel_StockInfo);
            infoPanel2 = new InfoPanelBuilder(Panel_StockInfo2);

            Helpers.HighlightButton(Btn1Y, new[] { Btn1M, Btn3M, Btn6M, Btn1Y, Btn2Y, BtnAll });
            Helpers.HighlightButton(null, new[] { BtnVol, BtnMacd, BtnRsi });
            if (_showVol) Helpers.HighlightButton(BtnVol);
            if (_showMacd) Helpers.HighlightButton(BtnMacd);
            if (_showRsi) Helpers.HighlightButton(BtnRsi);
            SyncDrawMargin();
            LoadChart();
        };
    }

    // ══════════════════════════════════════════════════════════
    //  PlotArea 동기화 — Y축 라벨 너비 차이로 인한 좌우 어긋남 해소
    //  DrawMargin(Margin): 모든 차트의 PlotArea 마진을 동일값으로 고정.
    //  Right=52: Price Y축 라벨("75,000" 6자리+콤마, Consolas 9pt) 기준.
    //  Left=0:   각 차트 좌측에 Y축 없음 → 별도 여백 불필요.
    //  이 값으로 모든 서브차트의 PlotArea 좌·우 경계가 메인 차트와 일치.
    // ══════════════════════════════════════════════════════════
    private void SyncDrawMargin()
    {
        // Margin(left, top, right, bottom) — float
        var margin = new LiveChartsCore.Measure.Margin(0f, 0f, 52f, 0f);

        MainChart.DrawMargin  = margin;
        VolChart.DrawMargin   = margin;
        MacdChart.DrawMargin  = margin;
        RsiChart.DrawMargin   = margin;
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
        RsiYAxes.Add(new Axis
        {
            Labeler           = v => v.ToString("F0"),
            LabelsPaint       = new SolidColorPaint(SKColor.Parse("#6C7086")),
            SeparatorsPaint   = new SolidColorPaint(SKColor.Parse("#313244")),
            TextSize          = 9,
            Position          = LiveChartsCore.Measure.AxisPosition.End,
            MinLimit          = 0,
            MaxLimit          = 100,
            // 0, 30, 50, 70, 100 위치에만 grid line + label 표시
            CustomSeparators  = new double[] { 0, 30, 50, 70, 100 },
        });
    }

    // X값: 순수 정수 인덱스 (0, 1, 2, ...)
    // Labeler: index → _renderData[index].Date 문자열
    private Axis MakeXAxis(bool showLabels = true) => new()
    {
        Labeler = v =>
        {
            var i = (int)Math.Round(v);
            if (i < 0 || i >= _renderData.Count) return "";
            var d = _renderData[i].Date;
            return d.Month == 1 && d.Day <= 7
                ? d.ToString("yy/MM")
                : d.ToString("MM/dd");
        },
        UnitWidth       = 1,
        MinStep         = 20,   // 최소 20영업일(~1개월) 간격 — label 겹침 방지
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

    // i → X값(double) 헬퍼: 순수 정수 인덱스
    private static double XT(int i) => (double)i;

    // ══════════════════════════════════════════════════════════
    //  데이터 로드
    // ══════════════════════════════════════════════════════════
    private void LoadInfo_ReturnRatio(string ticker)
    {
        //var infoPanel = new InfoPanelBuilder(Panel_StockInfo);
        infoPanel?.Clear();
        infoPanel2?.Clear();

        var cache = _db.GetStockCache(ticker);
        if (cache is null)
        {
            //TxtStockInfo_ReturnRatio.Text = "no data";
            infoPanel?.Text("no data");
            infoPanel2?.Text("no data");
            MiniChart_ReturnRatio.Series  = Array.Empty<ISeries>();
            return;
        }


        infoPanel?.AddCard(card =>
        {
            card.Header3(cache.Name)
                .Text($"{cache.Ticker} {cache.Market}.{cache.SecurityType}")
                .Metric("Per", cache.Per.ToString("#,##0.0"), 40, Brushes.White)
                .Metric("Pbr", cache.Pbr.ToString("#,##0.0"), 40, Brushes.White)
                .Metric("Eps", cache.Eps.ToString("#,##0"), 40, Brushes.White)
                .Metric("Roe", cache.Roe.ToString("#,##0.0"), 40, Brushes.White);
        });
//Per: 40.65
//Pbr: 4.199999809265137
//Roe: 0
//Eps: 6605
//Atr14: 18607.14285714286
//AtrPercent: 6.75
//High52W: 299500
//Low52W: 53700
//VolumeAvg20D: 29859078.7
//DistanceFromHigh: -8.01

        infoPanel?.AddCard(card =>
        {
            card.Metric("price", cache.CurrentPrice.ToString("#,##0"), 40, Brushes.White)
                .MetricColoredDigit("high", cache.DistanceFromHigh)
                .Text(cache.Low52W.ToString("#,##0") + " ~ " + cache.High52W.ToString("#,##0"), InfoPanelBuilder.InfoTone.Muted)
                .Separator()
                .MetricColoredDigit("1M", cache.Ret1M)
                .MetricColoredDigit("3M", cache.Ret3M)
                .MetricColoredDigit("6M", cache.Ret6M)
                .MetricColoredDigit("1Y", cache.Ret1Y)
                .MetricColoredDigit("RS", cache.Rs)
                .MetricColoredDigit("RS", cache.Rs)
                .Metric("Atr14", cache.Atr14.ToString("#,##0"), 40, Brushes.White)
                .MetricColoredDigit("Atr%", cache.AtrPercent)
                .Metric("Vol", (cache.VolumeAvg20D/1000).ToString("#,##0T"), 40, Brushes.White);
        });
        //static string Signed(double v) => v.ToString("+0.0;-0.0;0") + "%";

        //ReturnMetrics.Add(new("1M", cache.Ret1M.ToString("+0.0;-0.0;0") + "%", cache.Ret1M >= 0 ? StatusColors.Info : StatusColors.Error));
        //infoPanel.Header3(cache.Name);
        //infoPanel.Text($"{cache.Ticker} {cache.Market}.{cache.SecurityType}");
        //infoPanel.MetricColoredDigit("price", cache.CurrentPrice, 40, "#,##0", null);
        //infoPanel.MetricColoredDigit("1M", cache.Ret1M);
        //infoPanel.MetricColoredDigit("3M", cache.Ret3M);
        //infoPanel.MetricColoredDigit("6M", cache.Ret6M);
        //infoPanel.MetricColoredDigit("1Y", cache.Ret1Y);
        //infoPanel.MetricColoredDigit("RS", cache.Rs);

        var props = cache.GetType().GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var sb = new System.Text.StringBuilder();
        foreach (var p in props)
            sb.AppendLine($"{p.Name}: {p.GetValue(cache)}");
        infoPanel2?.Text(sb.ToString().TrimEnd());
        //TxtStockInfo.Text = sb.ToString().TrimEnd();

        // ── MiniChart: Line ─────────────────────────────────
        // X: 0=1M, 1=3M, 2=6M, 3=1Y  (ObservablePoint)
        static ObservablePoint[] ToPoints(double? r1m, double? r3m, double? r6m, double? r1y)
            => new[]
            {
                new ObservablePoint(0, r1m),
                new ObservablePoint(1, r3m),
                new ObservablePoint(2, r6m),
                new ObservablePoint(3, r1y),
            };

        var stockPts = ToPoints(cache.Ret1M, cache.Ret3M, cache.Ret6M, cache.Ret1Y);

        var series = new List<ISeries>
        {
            new LineSeries<ObservablePoint>
            {
                Name   = ticker,
                Values = stockPts,
                Stroke = new SolidColorPaint(SKColor.Parse("#89B4FA")) { StrokeThickness = 2 },
                Fill   = null,
                GeometrySize   = 5,
                GeometryFill   = new SolidColorPaint(SKColor.Parse("#89B4FA")),
                GeometryStroke = null,
                LineSmoothness = 0,
            },
        };

        var kospi = _db.GetStockCache("IDX_KOSPI");
        if (kospi is not null)
        {
            var kospiPts = ToPoints(kospi.Ret1M, kospi.Ret3M, kospi.Ret6M, kospi.Ret1Y);
            series.Add(new LineSeries<ObservablePoint>
            {
                Name   = "KOSPI",
                Values = kospiPts,
                Stroke = new SolidColorPaint(SKColor.Parse("#F38BA8"))
                {
                    StrokeThickness = 1,
                    PathEffect = new LiveChartsCore.SkiaSharpView.Painting.Effects.DashEffect(new float[] { 4, 3 })
                },
                Fill   = null,
                GeometrySize   = 4,
                GeometryFill   = new SolidColorPaint(SKColor.Parse("#F38BA8")),
                GeometryStroke = null,
                LineSmoothness = 0,
            });
        }

        // 0 기준선
        series.Add(new LineSeries<ObservablePoint>
        {
            Name   = "",
            Values = new ObservablePoint[] { new(0, 0), new(1, 0), new(2, 0), new(3, 0) },
            Stroke = new SolidColorPaint(SKColor.Parse("#45475A")) { StrokeThickness = 1 },
            Fill   = null,
            GeometrySize = 0, GeometryFill = null, GeometryStroke = null,
            LineSmoothness = 0,
        });

        MiniChart_ReturnRatio.Series = series.ToArray();

        MiniChart_ReturnRatio.XAxes = new Axis[]
        {
            new Axis
            {
                Labels          = new[] { "1M", "3M", "6M", "1Y" },
                MinStep         = 1,
                LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6C7086")),
                SeparatorsPaint = null,
                TextSize        = 8,
            }
        };

        MiniChart_ReturnRatio.YAxes = new Axis[]
        {
            new Axis
            {
                Labeler         = v => $"{v:F0}%",
                LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6C7086")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
                TextSize        = 8,
            }
        };

        MiniChart_ReturnRatio.LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden;
    }

    private void LoadChart()
    {
        var ticker = TxtTickerCode.Text.Trim().ToUpper();
        if (string.IsNullOrEmpty(ticker)) return;

        App.Config().LastTicker = ticker;
        _currentTicker = ticker;
        _currentCache = _db.GetStockCache(ticker) ?? new StockCache { Ticker = ticker };

        try
        {
            // ── stocks 테이블에 없으면 신규등록 Popup 표시 ─────────────
            if (!_db.ExistTicker("stocks", ticker))
            {
                OpenRegisterPopup(ticker);
                return;
            }


            RatingCtrl.Rating = _currentCache.Rating;// _db.GetStockInfo(ticker).rating;
            LoadInfo_ReturnRatio(ticker);
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
                Helpers.StatusWarning(StatusChanged, $"no data: {ticker}");
                return;
            }

            ApplyPeriod();
            TxtChartDataLoadingInfo.Text =
                $"{_allData.Count:N0}day. {_allData.First().Date:yyyy-MM-dd} ~ {_allData.Last().Date:yyyy-MM-dd}";
            Helpers.StatusSuccess(StatusChanged, 
                $"{ticker}  {_allData.Count:N0}일  " +
                $"{_allData.First().Date:yyyy-MM-dd} ~ {_allData.Last().Date:yyyy-MM-dd}");
        }
        catch (Exception ex) { Helpers.StatusException(StatusChanged, ex, "오류"); }
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

        // X축 범위를 _renderData 인덱스 기준으로 동기화
        var xMin = 0.0;
        var xMax = (double)(data.Count - 1);
        foreach (var ax in new[] { XAxes, VolXAxes, MacdXAxes, RsiXAxes })
        {
            if (ax.Count == 0) continue;
            ax[0].MinLimit = xMin;
            ax[0].MaxLimit = xMax;
        }

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
        var pts = data.Select((d, i) => new ObservablePoint(i, d.AdjClose)).ToList();
        Series.Add(new LineSeries<ObservablePoint>
        {
            Values         = new ObservableCollection<ObservablePoint>(pts),
            Fill           = new SolidColorPaint(SKColor.Parse("#89B4FA").WithAlpha(25)),
            Stroke         = new SolidColorPaint(SKColor.Parse("#89B4FA")) { StrokeThickness = 2 },
            GeometrySize   = 0, GeometryFill = null, GeometryStroke = null,
            LineSmoothness = 0, Name = _currentTicker,
        });
    }

    private void RenderCandle(List<OhlcvRow> data)
    {
        // FinancialPoint의 DateTime.Ticks = i (인덱스)
        // → X값이 ObservablePoint(i, ...) 와 동일한 스케일
        // UnitWidth=1 이므로 정수 인덱스 기준으로 정렬됨
        var pts = data.Select((d, i) =>
        {
            var r = d.Close > 0 ? d.AdjClose / d.Close : 1.0;
            return new FinancialPoint(new DateTime(i),   // Ticks=i → X=i
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
        var pts    = new List<ObservablePoint>();
        for (int i = period - 1; i < data.Count; i++)
        {
            var avg = prices.Skip(i - period + 1).Take(period).Average();
            pts.Add(new ObservablePoint(i, avg));
        }
        var color = MaColors[period];
        Series.Add(new LineSeries<ObservablePoint>
        {
            Values         = new ObservableCollection<ObservablePoint>(pts),
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

        var pts = data.Select((d, i) => new ObservablePoint(i, d.Volume)).ToList();
        VolSeries.Add(new ColumnSeries<ObservablePoint>
        {
            Values      = new ObservableCollection<ObservablePoint>(pts),
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
        _macdCache.Clear();
        if (!_showMacd || data.Count < 26) return;

        var prices = data.Select(d => d.AdjClose).ToList();
        var ema12  = CalcEma(prices, 12);
        var ema26  = CalcEma(prices, 26);

        var macdVals = new double[data.Count];
        for (int i = 25; i < data.Count; i++)
            macdVals[i] = ema12[i] - ema26[i];

        var sigSlice = CalcEma(macdVals.Skip(25).ToList(), 9);

        var macdLine   = new List<ObservablePoint>();
        var signalLine = new List<ObservablePoint>();
        var histogram  = new List<ObservablePoint>();

        for (int i = 25; i < data.Count; i++)
        {
            var mv  = macdVals[i];
            var si  = i - 25;
            macdLine.Add(new ObservablePoint(i, mv));
            if (si >= 8)
            {
                var sv = sigSlice[si];
                signalLine.Add(new ObservablePoint(i, sv));
                histogram.Add(new ObservablePoint(i, mv - sv));
                _macdCache[i] = (mv, sv, mv - sv);
            }
            else
            {
                _macdCache[i] = (mv, double.NaN, double.NaN);
            }
        }

        MacdSeries.Add(new LineSeries<ObservablePoint>
        {
            Values         = new ObservableCollection<ObservablePoint>(macdLine),
            Stroke         = new SolidColorPaint(SKColor.Parse("#89B4FA")) { StrokeThickness = 1 },
            Fill           = null, GeometrySize = 0, GeometryFill = null, GeometryStroke = null,
            LineSmoothness = 0, Name = "MACD",
        });
        MacdSeries.Add(new LineSeries<ObservablePoint>
        {
            Values         = new ObservableCollection<ObservablePoint>(signalLine),
            Stroke         = new SolidColorPaint(SKColor.Parse("#F38BA8")) { StrokeThickness = 1 },
            Fill           = null, GeometrySize = 0, GeometryFill = null, GeometryStroke = null,
            LineSmoothness = 0, Name = "Signal",
        });
        MacdSeries.Add(new ColumnSeries<ObservablePoint>
        {
            Values      = new ObservableCollection<ObservablePoint>(histogram),
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
        _rsiCache.Clear();
        if (!_showRsi || data.Count < 15) return;

        var prices = data.Select(d => d.AdjClose).ToList();
        var pts    = new List<ObservablePoint>();

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
            pts.Add(new ObservablePoint(i, rsi));
            _rsiCache[i] = rsi;
        }

        RsiSeries.Add(new LineSeries<ObservablePoint>
        {
            Values         = new ObservableCollection<ObservablePoint>(pts),
            Stroke         = new SolidColorPaint(SKColor.Parse("#CBA6F7")) { StrokeThickness = 1 },
            Fill           = null, GeometrySize = 0, GeometryFill = null, GeometryStroke = null,
            LineSmoothness = 0, Name = "RSI(14)",
        });
    }

    // ══════════════════════════════════════════════════════════
    //  수급 오버레이
    //  VolSeries[0] = 거래량 bar (RenderVolume)
    //  VolSeries[1] = 외인순매수 line  (ScalesYAt=1)
    //  VolSeries[2] = 기관순매수 line  (ScalesYAt=1)
    //  VolYAxes[0]  = 거래량 Y (End)
    //  VolYAxes[1]  = 수급 Y   (Start) — 처음 한 번만 추가
    // ══════════════════════════════════════════════════════════
    private void RenderSupply(List<OhlcvRow> data)
    {
        // 이전 수급 series 제거 (거래량 bar는 유지)
        for (int i = VolSeries.Count - 1; i >= 0; i--)
            if (VolSeries[i].Name is "외인순매수" or "기관순매수")
                VolSeries.RemoveAt(i);

        _supplyCache.Clear();
        if (data.Count == 0) return;

        // 수급 Y축: VolYAxes[1] — 없을 때만 추가
        if (VolYAxes.Count < 2)
            VolYAxes.Add(MakeYAxis(v =>
            {
                var m = v / 10_000.0;
                return Math.Abs(m) >= 1 ? $"{m:F0}만" : v.ToString("N0");
            }, minLimit: null, maxLimit: null));

        var dateFrom = data.First().Date.ToString("yyyy-MM-dd");
        var dateTo   = data.Last().Date.ToString("yyyy-MM-dd");

        DataTable dt;
        try
        {
            dt = _db.Query($"""
                SELECT date, inst_net_buy, foreign_net_buy
                FROM supply
                WHERE ticker = '{_currentTicker}'
                  AND date >= '{dateFrom}'
                  AND date <= '{dateTo}'
                ORDER BY date ASC
                """);
        }
        catch { return; }

        if (dt.Rows.Count == 0) return;

        // date → renderData index 역매핑
        var dateToIdx = data.Select((r, i) => (r.Date.Date, i))
                            .ToDictionary(x => x.Date, x => x.i);

        var fgnPts  = new List<ObservablePoint>();
        var instPts = new List<ObservablePoint>();

        foreach (DataRow r in dt.Rows)
        {
            DateTime d;
            if      (r[0] is DateTime dt0)  d = dt0;
            else if (r[0] is DateOnly  do0) d = do0.ToDateTime(TimeOnly.MinValue);
            else if (!DateTime.TryParse(r[0]?.ToString(), out d)) continue;

            if (!dateToIdx.TryGetValue(d.Date, out var idx)) continue;

            long inst = r[1] is DBNull || r[1] is null ? 0 : Convert.ToInt64(r[1]);
            long fgn  = r[2] is DBNull || r[2] is null ? 0 : Convert.ToInt64(r[2]);

            if (inst != 0) instPts.Add(new ObservablePoint(idx, inst));
            if (fgn  != 0) fgnPts.Add(new ObservablePoint(idx, fgn));

            // 툴팁 캐시: inst/fgn 중 하나라도 있으면 저장
            if (inst != 0 || fgn != 0)
                _supplyCache[idx] = (inst, fgn);
        }

        if (fgnPts.Count > 0)
            VolSeries.Add(new LineSeries<ObservablePoint>
            {
                Name           = "외인순매수",
                Values         = new ObservableCollection<ObservablePoint>(fgnPts),
                Stroke         = new SolidColorPaint(SKColor.Parse("#F38BA8")) { StrokeThickness = 1 },
                Fill           = null,
                GeometrySize   = 0, GeometryFill = null, GeometryStroke = null,
                LineSmoothness = 0,
                ScalesYAt      = 1,
            });

        if (instPts.Count > 0)
            VolSeries.Add(new LineSeries<ObservablePoint>
            {
                Name           = "기관순매수",
                Values         = new ObservableCollection<ObservablePoint>(instPts),
                Stroke         = new SolidColorPaint(SKColor.Parse("#A6E3A1")) { StrokeThickness = 1 },
                Fill           = null,
                GeometrySize   = 0, GeometryFill = null, GeometryStroke = null,
                LineSmoothness = 0,
                ScalesYAt      = 1,
            });
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

    //private void HighlightPeriodBtn(Button active, IEnumerable<Button> all)
    //{
    //    foreach (var b in all)
    //        b.Foreground = System.Windows.Media.Brushes.Gray;
    //    active.Foreground = (System.Windows.Media.Brush)(TryFindResource("AccentBlueBrush")
    //        ?? System.Windows.Media.Brushes.LightBlue);
    //}

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

            SupplyMetrics.Clear();
            if (dt.Rows.Count > 0)
            {
                var r = dt.Rows[0];
                long inst = r[1] is DBNull || r[1] is null ? 0 : Convert.ToInt64(r[1]);
                long fgn  = r[2] is DBNull || r[2] is null ? 0 : Convert.ToInt64(r[2]);
                long iAmt = r[3] is DBNull || r[3] is null ? 0 : Convert.ToInt64(r[3]);
                long fAmt = r[4] is DBNull || r[4] is null ? 0 : Convert.ToInt64(r[4]);

                SupplyMetrics.Add(new("기관(주)", FormatNet(inst), ColorForNet(inst)));
                SupplyMetrics.Add(new("외인(주)", FormatNet(fgn),  ColorForNet(fgn)));
                SupplyMetrics.Add(new("기관(억)", FormatAmt(iAmt), ColorForNet(iAmt)));
                SupplyMetrics.Add(new("외인(억)", FormatAmt(fAmt), ColorForNet(fAmt)));

                TxtSupplyDate.Text = $" ({r[0]})";
            }
            else
            {
                LoadSupplyNA();
                TxtSupplyDate.Text = "";
            }
        }
        catch (Exception ex)
        {
            LoadSupplyNA("ERR", StatusColors.Error);
            Helpers.StatusException(StatusChanged, ex, "수급 데이터 로드 오류");
        }

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

                FundamentalMetrics.Clear();
                FundamentalMetrics.Add(new("PER", per == 0 ? "-" : per.ToString("F1"), "#F0F15E"));
                FundamentalMetrics.Add(new("PBR", pbr == 0 ? "-" : pbr.ToString("F2"), "#F0F15E"));
                FundamentalMetrics.Add(new("EPS", eps == 0 ? "-" : eps.ToString("N0"), "#F0F15E"));
                FundamentalMetrics.Add(new("ROE", roe == 0 ? "-" : roe.ToString("F1") + "%", "#F0F15E"));

                TxtFundDate.Text = $" ({r[0]})";
            }
            else
            {
                LoadFundamentalNA();
                TxtFundDate.Text = "";
            }
        }
        catch (Exception ex)
        {
            LoadFundamentalNA("ERR", StatusColors.Error);
            Helpers.StatusException(StatusChanged, ex, "재무 데이터 로드 오류");
        }

        try
        {
            var sql = $"""
                SELECT id, date, title, target_price, writer, filepath
                FROM pdf_reports WHERE ticker='{TxtStockName.Text}'
                ORDER BY date DESC LIMIT 5000
                """;
            var dtReports = _db.Query(sql);
            GridReport.ItemsSource = dtReports.DefaultView;

            // 리포트 목록(최신순)에서 가장 첫 번째로 발견되는 유효한 목표주가를 찾아 상승여력을 계산합니다.
            double targetPrice = 0;
            foreach (DataRow row in dtReports.Rows)
            {
                if (row["target_price"] != DBNull.Value && double.TryParse(row["target_price"].ToString(), out var tp) && tp > 0)
                {
                    targetPrice = tp;
                    break;
                }
            }

            if (targetPrice > 0 && _currentCache.CurrentPrice > 0)
            {
                // 상승여력(Upside) = (목표가 / 현재가 - 1) * 100
                var upside = (targetPrice / _currentCache.CurrentPrice - 1) * 100;
                Upside.Text = upside.ToString("+0.00;-0.00;0") + "%";
            }
            else
            {
                Upside.Text = "-";
            }

        }
        catch { GridReport.ItemsSource = null; }

        // ── GridGroup: v_active_group_map ─────────────────────
        try
        { 
            var dt = _db.Query($"""
                SELECT kind, group_name, group_rating, weight
                FROM v_active_group_map
                WHERE ticker = '{ticker}'
                ORDER BY group_rating DESC, kind
                """);
            GridGroup.ItemsSource = dt.DefaultView;
        }
        catch { GridGroup.ItemsSource = null; }

        // ── MiniChart_Fundamentals: PER / PBR 시계열 ──────────
        LoadMiniChartFundamentals(ticker);
    }

    private string ColorForNet(long v) => v >= 0 ? StatusColors.Info : StatusColors.Error;

    private void LoadSupplyNA(string val = "N/A", string color = "#CDD6F4")
    {
        SupplyMetrics.Clear();
        SupplyMetrics.Add(new("기관(주)", val, color));
        SupplyMetrics.Add(new("외인(주)", val, color));
        SupplyMetrics.Add(new("기관(억)", val, color));
        SupplyMetrics.Add(new("외인(억)", val, color));
    }

    private void LoadFundamentalNA(string val = "N/A", string color = "#F0F15E")
    {
        FundamentalMetrics.Clear();
        FundamentalMetrics.Add(new("PER", val, color));
        FundamentalMetrics.Add(new("PBR", val, color));
        FundamentalMetrics.Add(new("EPS", val, color));
        FundamentalMetrics.Add(new("ROE", val, color));
    }

    // ══════════════════════════════════════════════════════════
    //  MiniChart_Fundamentals: PER / PBR 시계열 (최근 8분기)
    // ══════════════════════════════════════════════════════════
    // ══════════════════════════════════════════════════════════
    //  MiniChart_Fundamentals — Base-100 정규화
    //
    //  PER / PBR / EPS 의 절대값 스케일이 달라 한 Y축에 표시 불가.
    //  → 각 지표의 첫 유효값(≠0)을 100으로 정규화하여 단일 Y축 표시.
    //    Y=100 기준선 추가.  음수 EPS 허용 (Base는 첫 양수값 사용).
    //    첫 값이 0이면 다음 유효값으로 fallback.
    //  X축: Labeler 함수 방식 (Labels 배열 중복 버그 회피)
    // ══════════════════════════════════════════════════════════
    private void LoadMiniChartFundamentals(string ticker)
    {
        //var infoPanel = new InfoPanelBuilder(Panel_StockInfo);
        //infoPanel2?.Clear();
        //var card = infoPanel2?.AddCard();

        try
        {
            var dt = _db.Query($"""
                SELECT report_date, per, pbr, eps
                FROM fundamentals
                WHERE ticker = '{ticker}'
                  AND report_date IS NOT NULL
                  AND report_date >= (CURRENT_DATE - INTERVAL 1 YEAR)
                ORDER BY report_date ASC
                """);

            if (dt.Rows.Count == 0)
            {
                MiniChart_Fundamentals.Series = Array.Empty<ISeries>();
                return;
            }

            var n       = dt.Rows.Count;
            var xLabels = new string[n];
            //Debug.WriteLine($"[MiniFund] ticker={ticker}, dt.Rows.Count={n}");
            //for (int i = 0; i < n; i++)
            //{
            //    var r = dt.Rows[i];
            //    Debug.WriteLine(
            //        $"[MiniFund][row {i:D2}] report_date={r[0]}, per={r[1]}, pbr={r[2]}, eps={r[3]}"
            //    );
            //}

            // 원본 값 수집
            var perRaw = new double?[n];
            var pbrRaw = new double?[n];
            var epsRaw = new double?[n];

            for (int i = 0; i < n; i++)
            {
                var r   = dt.Rows[i];
                var raw = r[0]?.ToString() ?? "";
                xLabels[i] = raw.Length >= 7 ? raw[..7] : raw;

                if (double.TryParse(r[1]?.ToString(), out var per) && per > 0)  perRaw[i] = per;
                if (double.TryParse(r[2]?.ToString(), out var pbr) && pbr > 0)  pbrRaw[i] = pbr;
                if (double.TryParse(r[3]?.ToString(), out var eps) && eps != 0) epsRaw[i] = eps;
            }

            int perValid = perRaw.Count(v => v.HasValue);
            int pbrValid = pbrRaw.Count(v => v.HasValue);
            int epsValid = epsRaw.Count(v => v.HasValue);
            // Debug.WriteLine($"[MiniFund] valid-count PER={perValid}, PBR={pbrValid}, EPS={epsValid}");
            // Debug.WriteLine($"[MiniFund] raw-sample EPS={string.Join(", ", epsRaw.Take(6).Select(v => v?.ToString("N0") ?? "null"))}");

            // Base-100 정규화: 첫 유효값(>0인 값) 기준
            static List<ObservablePoint> Normalize(double?[] raw)
            {
                double?  first   = raw.FirstOrDefault(v => v.HasValue && v.Value != 0);
                double   baseVal = first ?? 0;
                if (baseVal == 0) return [];
                var pts = new List<ObservablePoint>();
                for (int i = 0; i < raw.Length; i++)
                    if (raw[i].HasValue)
                        pts.Add(new ObservablePoint(i, raw[i]!.Value / baseVal * 100.0));
                return pts;
            }

            var perPts = Normalize(perRaw);
            var pbrPts = Normalize(pbrRaw);
            var epsPts = Normalize(epsRaw);

            //static string PtRange(List<ObservablePoint> pts)
            //{
            //    if (pts.Count == 0) return "empty";
            //    var min = pts.Min(p => p.Y);
            //    var max = pts.Max(p => p.Y);
            //    return $"{min:F2}..{max:F2}";
            //}

            // Debug.WriteLine($"[MiniFund] norm-count PER={perPts.Count}, PBR={pbrPts.Count}, EPS={epsPts.Count}");
            // Debug.WriteLine($"[MiniFund] norm-range PER={PtRange(perPts)}, PBR={PtRange(pbrPts)}, EPS={PtRange(epsPts)}");
            // Debug.WriteLine($"[MiniFund] norm-sample EPS={string.Join(", ", epsPts.Take(6).Select(p => $"({p.X:F0},{p.Y:F2})"))}");

            var series = new List<ISeries>();

            // Y=100 기준선
            series.Add(new LineSeries<ObservablePoint>
            {
                Name           = "",
                Values         = new ObservablePoint[] { new(-0.5, 100), new(n - 0.5, 100) },
                Stroke         = new SolidColorPaint(SKColor.Parse("#45475A")) { StrokeThickness = 1 },
                Fill           = null,
                GeometrySize   = 0, GeometryFill = null, GeometryStroke = null,
                LineSmoothness = 0,
            });

            if (perPts.Count > 0)
                series.Add(new LineSeries<ObservablePoint>
                {
                    Name           = "PER",
                    Values         = new ObservableCollection<ObservablePoint>(perPts),
                    Stroke         = new SolidColorPaint(SKColor.Parse("#89B4FA")) { StrokeThickness = 2 },
                    Fill           = null,
                    GeometrySize   = 0,
                    GeometryFill   = null,
                    GeometryStroke = null,
                    LineSmoothness = 0,
                });

            if (pbrPts.Count > 0)
                series.Add(new LineSeries<ObservablePoint>
                {
                    Name           = "PBR",
                    Values         = new ObservableCollection<ObservablePoint>(pbrPts),
                    Stroke         = new SolidColorPaint(SKColor.Parse("#A6E3A1")) { StrokeThickness = 2 },
                    Fill           = null,
                    GeometrySize   = 0,
                    GeometryFill   = null,
                    GeometryStroke = null,
                    LineSmoothness = 0,
                });

            if (epsPts.Count > 0)
                series.Add(new LineSeries<ObservablePoint>
                {
                    Name           = "EPS",
                    Values         = new ObservableCollection<ObservablePoint>(epsPts),
                    Stroke         = new SolidColorPaint(SKColor.Parse("#F9E2AF")) { StrokeThickness = 2 },
                    Fill           = null,
                    GeometrySize   = 0,
                    GeometryFill   = null,
                    GeometryStroke = null,
                    LineSmoothness = 0,
                });

            MiniChart_Fundamentals.Series = series.ToArray();

            MiniChart_Fundamentals.XAxes = new Axis[]
            {
                new Axis
                {
                    Labeler = v =>
                    {
                        var i = (int)Math.Round(v);
                        return (i >= 0 && i < xLabels.Length) ? xLabels[i] : "";
                    },
                    UnitWidth       = 1,
                    MinStep         = 1,
                    MinLimit        = -0.5,
                    MaxLimit        = n - 0.5,
                    LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6C7086")),
                    SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
                    TextSize        = 8,
                }
            };

            // 단일 Y축 — Base-100 기준 (이중 Y축 불필요)
            MiniChart_Fundamentals.YAxes = new Axis[]
            {
                new Axis
                {
                    Labeler         = v => v.ToString("F0"),
                    LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6C7086")),
                    SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
                    TextSize        = 8,
                    Position        = LiveChartsCore.Measure.AxisPosition.End,
                },
            };

            MiniChart_Fundamentals.LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden;
        }
        catch
        {
            MiniChart_Fundamentals.Series = Array.Empty<ISeries>();
        }
    }

    private static string FormatNet(long v) => v == 0 ? "0" : v > 0 ? $"+{v:N0}" : $"{v:N0}";
    private static string FormatAmt(long v)
    {
        var e = v / 100_000_000.0;
        if (Math.Abs(e) >= 1) return e > 0 ? $"+{e:F0}" : $"{e:F0}";
        var m = v / 10_000.0;
        return m > 0 ? $"+{m:F0}만" : $"{m:F0}만";
    }

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
        Helpers.HighlightButton(btn, new[] { Btn1M, Btn3M, Btn6M, Btn1Y, Btn2Y, BtnAll });
        ApplyPeriod();
    }

    private void BtnExternalLink_Click(object sender, RoutedEventArgs e) =>
        Helpers.OpenExternalLink(TxtTickerCode.Text);

    // ══════════════════════════════════════════════════════════
    //  Crosshair — 차트 영역 전체(Row 3~6)에 가로/세로 교차선 표시
    //  Canvas는 IsHitTestVisible=False → 차트 이벤트 통과.
    //  ChartGrid 기준 Y좌표에서 헤더·툴바·수급패널 높이를 빼서
    //  Canvas(Row 3 시작) 기준 상대 Y를 계산.
    // ══════════════════════════════════════════════════════════
    private void ChartGrid_MouseMove(object sender, MouseEventArgs e)
    {
        // ChartGrid 기준 마우스 위치
        var pos = e.GetPosition(ChartGrid);

        // Row 0~2 누적 높이 계산 (헤더44 + 툴바32 + 수급패널Auto)
        double headerH = ChartGrid.RowDefinitions[0].ActualHeight
                       + ChartGrid.RowDefinitions[1].ActualHeight
                       + ChartGrid.RowDefinitions[2].ActualHeight;

        // Row 3~6 차트 영역 높이
        double chartAreaH = ChartGrid.RowDefinitions[3].ActualHeight
                          + ChartGrid.RowDefinitions[4].ActualHeight
                          + ChartGrid.RowDefinitions[5].ActualHeight
                          + ChartGrid.RowDefinitions[6].ActualHeight;

        double chartAreaW = ChartGrid.ActualWidth;

        // 마우스가 차트 영역(Row 3~6) 안에 있는지 확인
        if (pos.Y < headerH || pos.Y > headerH + chartAreaH)
        {
            CrosshairCanvas.Visibility = Visibility.Collapsed;
            TooltipPanel.Visibility    = Visibility.Collapsed;
            return;
        }

        CrosshairCanvas.Visibility = Visibility.Visible;

        // Canvas는 Row3 시작 → Canvas 기준 Y = ChartGrid Y − headerH
        double cy = pos.Y - headerH;
        double cx = pos.X;

        // 세로선: X 고정, Y는 Canvas 전체 높이
        CrossV.X1 = cx; CrossV.Y1 = 0;
        CrossV.X2 = cx; CrossV.Y2 = chartAreaH;

        // 가로선: Y 고정, X는 Canvas 전체 너비
        CrossH.X1 = 0;          CrossH.Y1 = cy;
        CrossH.X2 = chartAreaW; CrossH.Y2 = cy;

        // ── 툴팁 데이터 인덱스 계산 ──────────────────────────────
        // PlotArea: SyncDrawMargin 에서 Right=52px 고정.
        // PlotArea 좌단 = 0, 우단 = chartAreaW - 52
        const double rightMargin = 52.0;
        double plotW = chartAreaW - rightMargin;  // PlotArea 실제 픽셀 너비

        if (_renderData.Count == 0 || plotW <= 0)
        {
            TooltipPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // XAxes[0].MinLimit / MaxLimit 으로 현재 뷰 범위 취득
        double xMin = XAxes[0].MinLimit ?? 0;
        double xMax = XAxes[0].MaxLimit ?? (_renderData.Count - 1);
        double xRange = xMax - xMin;
        if (xRange <= 0)
        {
            TooltipPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // cx (Canvas X) → 데이터 인덱스
        // PlotArea 픽셀 범위 [0, plotW] → 데이터 범위 [xMin, xMax]
        double dataX = xMin + (cx / plotW) * xRange;
        int idx = (int)Math.Round(dataX);
        idx = Math.Max(0, Math.Min(_renderData.Count - 1, idx));

        var row = _renderData[idx];

        // ── 날짜 ──────────────────────────────────────────────
        TtDate.Text = row.Date.ToString("yyyy-MM-dd (ddd)");

        // ── 가격 ──────────────────────────────────────────────
        var priceSb = new System.Text.StringBuilder();
        priceSb.Append($"O {row.Open:N0}  H {row.High:N0}  L {row.Low:N0}  C {row.Close:N0}");
        if (Math.Abs(row.AdjClose - row.Close) > 1)
            priceSb.Append($"  Adj {row.AdjClose:N0}");
        TtPrice.Text = priceSb.ToString();

        // ── MA ────────────────────────────────────────────────
        if (_activeMas.Count > 0)
        {
            var prices = _renderData.Select(d => d.AdjClose).ToList();
            var maSb   = new System.Text.StringBuilder();
            foreach (var period in _activeMas.OrderBy(p => p))
            {
                if (idx >= period - 1)
                {
                    var avg = prices.Skip(idx - period + 1).Take(period).Average();
                    maSb.Append($"MA{period} {avg:N0}  ");
                }
            }
            TtMa.Text = maSb.ToString().TrimEnd();
        }
        else
        {
            TtMa.Text = "";
        }

        // ── VOL + 수급 ────────────────────────────────────────
        var volSb = new System.Text.StringBuilder();
        volSb.Append($"VOL {row.Volume:N0}");
        if (_supplyCache.TryGetValue(idx, out var supply))
        {
            static string Fmt(long v) => v > 0 ? $"+{v:N0}" : v.ToString("N0");
            volSb.Append($"  기관 {Fmt(supply.Inst)}  외인 {Fmt(supply.Fgn)}");
        }
        TtVol.Text = volSb.ToString();

        // ── MACD ──────────────────────────────────────────────
        if (_showMacd && _macdCache.TryGetValue(idx, out var macd))
        {
            TtMacd.Text = double.IsNaN(macd.Signal)
                ? $"MACD {macd.Macd:F3}"
                : $"MACD {macd.Macd:F3}  Sig {macd.Signal:F3}  Hist {macd.Hist:F3}";
        }
        else
        {
            TtMacd.Text = "";
        }

        // ── RSI ───────────────────────────────────────────────
        TtRsi.Text = (_showRsi && _rsiCache.TryGetValue(idx, out var rsi))
            ? $"RSI {rsi:F1}"
            : "";

        TooltipPanel.Visibility = Visibility.Visible;
    }

    private void ChartGrid_MouseLeave(object sender, MouseEventArgs e)
    {
        CrosshairCanvas.Visibility = Visibility.Collapsed;
        ShowTooltipAtLastIndex();
    }

    // 마우스가 차트 밖일 때 최신 인덱스(today) 기준 툴팁 고정 표시
    private void ShowTooltipAtLastIndex()
    {
        if (_renderData.Count == 0)
        {
            TooltipPanel.Visibility = Visibility.Collapsed;
            return;
        }

        int idx = _renderData.Count - 1;
        var row = _renderData[idx];

        TtDate.Text = row.Date.ToString("yyyy-MM-dd (ddd)");

        var priceSb = new System.Text.StringBuilder();
        priceSb.Append($"O {row.Open:N0}  H {row.High:N0}  L {row.Low:N0}  C {row.Close:N0}");
        if (Math.Abs(row.AdjClose - row.Close) > 1)
            priceSb.Append($"  Adj {row.AdjClose:N0}");
        TtPrice.Text = priceSb.ToString();

        if (_activeMas.Count > 0)
        {
            var prices = _renderData.Select(d => d.AdjClose).ToList();
            var maSb   = new System.Text.StringBuilder();
            foreach (var period in _activeMas.OrderBy(p => p))
            {
                if (idx >= period - 1)
                {
                    var avg = prices.Skip(idx - period + 1).Take(period).Average();
                    maSb.Append($"MA{period} {avg:N0}  ");
                }
            }
            TtMa.Text = maSb.ToString().TrimEnd();
        }
        else
        {
            TtMa.Text = "";
        }

        var volSb = new System.Text.StringBuilder();
        volSb.Append($"VOL {row.Volume:N0}");
        if (_supplyCache.TryGetValue(idx, out var supply))
        {
            static string Fmt(long v) => v > 0 ? $"+{v:N0}" : v.ToString("N0");
            volSb.Append($"  기관 {Fmt(supply.Inst)}  외인 {Fmt(supply.Fgn)}");
        }
        TtVol.Text = volSb.ToString();

        if (_showMacd && _macdCache.TryGetValue(idx, out var macd))
        {
            TtMacd.Text = double.IsNaN(macd.Signal)
                ? $"MACD {macd.Macd:F3}"
                : $"MACD {macd.Macd:F3}  Sig {macd.Signal:F3}  Hist {macd.Hist:F3}";
        }
        else
        {
            TtMacd.Text = "";
        }

        TtRsi.Text = (_showRsi && _rsiCache.TryGetValue(idx, out var rsi))
            ? $"RSI {rsi:F1}"
            : "";

        TooltipPanel.Visibility = Visibility.Visible;
    }

    private async void BtnDownloadData_Click(object sender, RoutedEventArgs e)
    {
        var ticker = TxtTickerCode.Text.Trim().ToUpper();
        if (string.IsNullOrEmpty(ticker)) return;
        if (sender is Button btn) btn.IsEnabled = false;
        try
        {
            var svc      = new PriceDownloadService(_db);
            var progress = new Progress<string>(msg => Helpers.StatusInfo(StatusChanged, msg));
            var (inserted, lastDate) = await svc.DownloadAsync(ticker, progress);
            string msg;
            if (inserted > 0)
            {
                _db.RebuildStockCache();
                LoadChart();
                msg = $"{ticker}: {inserted:N0}건 저장 완료 (최신 {lastDate})";
            }
            else
            {
                msg = $"{ticker}: 신규 데이터 없음 (최신 {lastDate})";
            }
            Helpers.StatusSuccess(StatusChanged, msg);
        }
        catch (Exception ex) { Helpers.StatusException(StatusChanged, ex, "다운로드 오류"); }
        finally { if (sender is Button btn2) btn2.IsEnabled = true; }
    }

    private void RatingCtrl_RatingChanged(int rating)
    {
        if (string.IsNullOrEmpty(_currentTicker)) return;
        try
        {
            _db.SetStockRating(rating, _currentTicker);
            Helpers.StatusSuccess(StatusChanged, $"{_currentTicker} rating 저장: {rating}");
        }
        catch (Exception ex) { Helpers.StatusException(StatusChanged, ex, "rating 저장 오류"); }
    }

    private void GridReportClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d) return;
        var cell = FindVisualParent<DataGridCell>(d);
        if (cell?.Column?.Header?.ToString() != "title") return;

        if (GridReport.SelectedItem is not DataRowView row) return;
        var filepath = row["filepath"]?.ToString();
        var (ok, message) = Helpers.OpenWithChrome(filepath);
        TxtStatus.Text = message;
        if (ok)
            Helpers.StatusInfo(StatusChanged, message);
        else
            Helpers.StatusError(StatusChanged, message);
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T target) return target;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    // ══════════════════════════════════════════════════════════
    //  신규종목 등록 Popup
    // ══════════════════════════════════════════════════════════

    // Popup을 열고 Yahoo 조회를 비동기로 시작
    private async void OpenRegisterPopup(string ticker)
    {
        TxtRegTicker.Text = ticker;
        TxtRegName.Text   = "";
        TxtRegMsg.Text    = "Yahoo 조회 중...";
        TxtRegMsg.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6C7086"));
        BtnRegSave.IsEnabled  = false;
        RegisterOverlay.Visibility = Visibility.Visible;

        // Yahoo 메타 비동기 조회
        var svc = new PriceDownloadService(_db);
        try
        {
            var meta = await svc.FetchStockMetaAsync(ticker);
            if (meta.HasValue)
            {
                var m = meta.Value;
                _pendingRegister = new RegisterState(ticker, m.LongName, m.Market, m.QuoteType);

                TxtRegName.Text = m.LongName ?? "";

                // Market 콤보박스 선택
                SelectComboByContent(CmbRegMarket, m.Market ?? "KP");

                // QuoteType → 유형 콤보박스
                var typeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ETF",   "ETF"   },
                    { "INDEX", "index" },
                    { "EQUITY","stock" },
                };
                var mapped = m.QuoteType is not null && typeMap.TryGetValue(m.QuoteType, out var t) ? t : "stock";
                SelectComboByContent(CmbRegType, mapped);

                TxtRegMsg.Text = $"✅ Yahoo 조회 성공 ({m.Market}, {m.QuoteType})\n" +
                                 $"   종목명을 확인/수정 후 등록하세요.";
                TxtRegMsg.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A6E3A1"));
            }
            else
            {
                _pendingRegister = new RegisterState(ticker, null, null, null);
                TxtRegMsg.Text = "⚠️ Yahoo 조회 실패. 종목명/Market/유형을 직접 입력하세요.";
                TxtRegMsg.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F9E2AF"));
            }
        }
        catch (Exception ex)
        {
            _pendingRegister = new RegisterState(ticker, null, null, null);
            TxtRegMsg.Text = $"❌ 오류: {ex.Message}";
            TxtRegMsg.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F38BA8"));
        }
        BtnRegSave.IsEnabled = true;
    }

    private void BtnRegCancel_Click(object sender, RoutedEventArgs e)
    {
        RegisterOverlay.Visibility = Visibility.Collapsed;
        _pendingRegister = null;
    }

    private async void BtnRegSave_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingRegister is null) return;

        var name   = TxtRegName.Text.Trim();
        var market = (CmbRegMarket.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "KP";
        var type   = (CmbRegType.SelectedItem   as ComboBoxItem)?.Content?.ToString() ?? "stock";

        if (string.IsNullOrEmpty(name))
        {
            TxtRegMsg.Text = "⚠️ 종목명을 입력하세요.";
            return;
        }

        BtnRegSave.IsEnabled = false;
        try
        {
            _db.UpsertStock(new Stock
            {
                Ticker = _pendingRegister.Ticker,
                Name = name,
                Market = market,
                SecurityType = type,
                UpdatedAt = DateTime.Now
            });
            //_db.UpsertStock(_pendingRegister.Ticker, name, market, type, null, type == "ETF");

            TxtStockName.Text = name;
            Helpers.StatusSuccess(StatusChanged, $"{_pendingRegister.Ticker} 등록 완료");

            RegisterOverlay.Visibility = Visibility.Collapsed;
            _pendingRegister = null;

            // 등록 후 자동으로 주가 다운로드 시도
            var inserted = await DownloadPrice(TxtTickerCode.Text.Trim().ToUpper());

            //var ticker  = TxtTickerCode.Text.Trim().ToUpper();
            //var svc     = new PriceDownloadService(_db);
            //var progress = new Progress<string>(msg => Helpers.StatusInfo(StatusChanged, msg));
            //var (inserted, lastDate) = await svc.DownloadAsync(ticker, progress);
            //if (inserted > 0) _db.RebuildStockCache();
            LoadChart();
        }
        catch (Exception ex)
        {
            TxtRegMsg.Text = $"❌ DB 저장 실패: {ex.Message}";
            TxtRegMsg.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F38BA8"));
            BtnRegSave.IsEnabled = true;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  종목 영구제거
    // ══════════════════════════════════════════════════════════

    private void BtnDeleteStock_Click(object sender, RoutedEventArgs e)
    {
        var ticker = TxtTickerCode.Text.Trim().ToUpper();
        if (string.IsNullOrEmpty(ticker)) return;

        var name = TxtStockName.Text;
        var result = MessageBox.Show(
            $"종목 [{ticker}] {name}의 모든 데이터를 영구제거합니다.\n\n" +
            $"• stock_cache\n• supply\n• fundamentals\n• daily_prices\n• stock_group_map\n• stocks\n\n" +
            $"이 작업은 되돌릴 수 없습니다. 진행하시겠습니까?",
            "종목 영구제거",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            _db.DeleteStockAllData(ticker);

            // UI 초기화
            _allData.Clear();
            _renderData = [];
            Series.Clear(); VolSeries.Clear(); MacdSeries.Clear(); RsiSeries.Clear();
            TxtStockName.Text = "-";
            TxtChartPriceInfo.Text = "";
            TxtChartDataLoadingInfo.Text = "";
            SupplyMetrics.Clear();
            FundamentalMetrics.Clear();
            infoPanel2?.Text("no data");
            GridReport.ItemsSource = null;

            Helpers.StatusWarning(StatusChanged, $"{ticker} 제거 완료");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"제거 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            Helpers.StatusException(StatusChanged, ex, $"{ticker} 제거 실패");
        }
    }

    private async void BtnDeletePrice_Click( object sender, EventArgs e )
    {
        var ticker = TxtTickerCode.Text.Trim().ToUpper();
        if (string.IsNullOrEmpty(ticker)) return;
        var name = TxtStockName.Text;
        var result = MessageBox.Show(
            $"종목 [{ticker}] {name}의 PriceInfo(daily_prices)를 제거합니다.\n\n" +
            $"• daily_prices 테이블에서 해당 ticker의 모든 행 삭제\n\n" +
            $"이 작업은 되돌릴 수 없습니다. 진행하시겠습니까?",
            "PriceInfo 제거",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            _db.DeletePriceData(ticker);
            Helpers.StatusWarning(StatusChanged, $"{ticker} PriceInfo 제거 완료");
            var inserted = await DownloadPrice(ticker);
            LoadChart();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"PriceInfo 삭제 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            Helpers.StatusException(StatusChanged, ex, $"{ticker} PriceInfo 제거 실패");
        }
    }

    private async Task<int> DownloadPrice(string ticker)
    {
        var svc = new PriceDownloadService(_db);
        var progress = new Progress<string>(msg => Helpers.StatusInfo(StatusChanged, msg));
        var (inserted, lastDate) = await svc.DownloadAsync(ticker, progress);
        if (inserted > 0) _db.RebuildStockCache();
        return inserted;
    }

    // 콤보박스 콘텐츠로 항목 선택
    private static void SelectComboByContent(ComboBox cmb, string content)
    {
        foreach (ComboBoxItem item in cmb.Items)
        {
            if (item.Content?.ToString()?.Equals(content, StringComparison.OrdinalIgnoreCase) == true)
            {
                cmb.SelectedItem = item;
                return;
            }
        }
        // 없으면 first 항목 선택
        if (cmb.Items.Count > 0) cmb.SelectedIndex = 0;
    }


    /// <summary>
    /// DataGrid의 셀 편집이 완료되었을 때 호출되어 DB에 변경 사항을 저장합니다.
    /// </summary>
    private void GridReport_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            if (e.Row.Item is not DataRowView row) return;

            // 바인딩된 소스 값이 업데이트될 시간을 주기 위해 Background 우선순위로 실행
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    using var conn = _db.OpenNativeConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE pdf_reports SET target_price = $1, analyze_status = 'done' WHERE id = $2";
                    cmd.Parameters.Add(new DuckDBParameter { Value = Convert.ToDouble(row["target_price"]) });
                    cmd.Parameters.Add(new DuckDBParameter { Value = Convert.ToInt64(row["id"]) });
                    cmd.ExecuteNonQuery();

                    TxtStatus.Text = $"ID {row["id"]} 목표주가 저장 완료: {row["target_price"]:N0}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"DB 업데이트 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void TextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.Dispatcher.BeginInvoke(new Action(() => tb.SelectAll()));
        }
    }
}
