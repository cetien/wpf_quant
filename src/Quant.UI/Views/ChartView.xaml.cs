using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Quant.Core.Infrastructure;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Quant.UI.Views;

public partial class ChartView : UserControl
{
    public event Action<string, string>? StatusChanged;

    public ObservableCollection<ISeries> Series { get; } = [];
    public ObservableCollection<Axis>    XAxes  { get; } = [];
    public ObservableCollection<Axis>    YAxes  { get; } = [];

    private readonly DbManager _db = DbManager.Instance;

    private List<(DateTime date, double close)> _allData = [];
    private int _periodDays = 365;

    public ChartView()
    {
        InitializeComponent();
        DataContext = this;
        InitAxes();
        Loaded += (_, _) => LoadChart();
    }

    private void InitAxes()
    {
        XAxes.Add(new Axis
        {
            Labeler         = v => new DateTime((long)v).ToString("yy/MM/dd"),
            LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6C7086")),
            SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
            TextSize        = 10,
        });
        YAxes.Add(new Axis
        {
            Labeler         = v => v.ToString("N0"),
            LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6C7086")),
            SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
            TextSize        = 10,
            Position        = LiveChartsCore.Measure.AxisPosition.End,
        });
    }

    private void LoadChart()
    {
        var ticker = TxtTicker.Text.Trim().ToUpper();
        if (string.IsNullOrEmpty(ticker)) return;
        try
        {
            var sql  = $"SELECT date, adj_close FROM daily_prices WHERE ticker='{ticker}' ORDER BY date ASC";
            var dt   = _db.Query(sql);
            _allData = dt.Rows.Cast<System.Data.DataRow>()
                .Select(r =>
                {
                    DateTime.TryParse(r[0]?.ToString(), out var d);
                    double.TryParse(r[1]?.ToString(), out var p);
                    return (date: d, close: p);
                })
                .Where(x => x.date != default && x.close > 0)
                .ToList();

            if (_allData.Count == 0)
            {
                TxtStatus.Text = $"데이터 없음: {ticker}";
                StatusChanged?.Invoke($"데이터 없음: {ticker}", "#F9E2AF");
                return;
            }
            //TxtName.Text = ticker;
            ApplyPeriod();
            StatusChanged?.Invoke(
                $"{ticker}  {_allData.Count:N0}일  {_allData.First().date:yyyy-MM-dd} ~ {_allData.Last().date:yyyy-MM-dd}",
                "#A6E3A1");
        }
        catch (Exception ex) { StatusChanged?.Invoke($"오류: {ex.Message}", "#F38BA8"); }
    }

    private void ApplyPeriod()
    {
        var data = _periodDays == 0
            ? _allData
            : _allData.Where(d => d.date >= DateTime.Today.AddDays(-_periodDays)).ToList();
        if (data.Count == 0) return;

        var points = data.Select(d => new DateTimePoint(d.date, d.close)).ToList();
        Series.Clear();
        Series.Add(new LineSeries<DateTimePoint>
        {
            Values         = new ObservableCollection<DateTimePoint>(points),
            Fill           = new SolidColorPaint(SKColor.Parse("#89B4FA").WithAlpha(25)),
            Stroke         = new SolidColorPaint(SKColor.Parse("#89B4FA")) { StrokeThickness = 2 },
            GeometrySize   = 0,
            GeometryFill   = null,
            GeometryStroke = null,
            LineSmoothness = 0,
            Name           = TxtTicker.Text.Trim().ToUpper(),
        });

        var hi  = data.MaxBy(d => d.close);
        var lo  = data.MinBy(d => d.close);
        var ret = (data.Last().close / data.First().close - 1) * 100;
        TxtInfo.Text = $"▲ {hi.close:N0}  ▼ {lo.close:N0}   상승률 {ret:+0.00;-0.00}%";
    }

    public void LoadTicker(string ticker, string name)
    {
		TxtTicker.Text = ticker.ToUpper();
		TxtName.Text = name;
        LoadChart();
    }

    private void BtnLoad_Click(object sender, RoutedEventArgs e) => LoadChart();

    private void TxtTicker_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) LoadChart();
    }

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
}
