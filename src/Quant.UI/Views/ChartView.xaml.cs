using DuckDB.NET.Data;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Quant.UI.Views;

public partial class ChartView : UserControl
{
    private static readonly string DbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "quant", "quant.duckdb");

    public event Action<string, string>? StatusChanged;

    // LiveCharts 바인딩 프로퍼티
    public ObservableCollection<ISeries>  Series { get; } = [];
    public ObservableCollection<Axis>     XAxes  { get; } = [];
    public ObservableCollection<Axis>     YAxes  { get; } = [];

    private List<(DateTime date, double close)> _allData = [];
    private int _periodDays = 365; // 기본 1Y

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
            Labeler    = v => new DateTime((long)v).ToString("yy/MM/dd"),
            LabelsPaint = new SolidColorPaint(SKColor.Parse("#6C7086")),
            SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
            TextSize   = 10,
        });
        YAxes.Add(new Axis
        {
            Labeler    = v => v.ToString("N0"),
            LabelsPaint = new SolidColorPaint(SKColor.Parse("#6C7086")),
            SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#313244")),
            TextSize   = 10,
            Position   = LiveChartsCore.Measure.AxisPosition.End,
        });
    }

    private void LoadChart()
    {
        var ticker = TxtTicker.Text.Trim().ToUpper();
        if (string.IsNullOrEmpty(ticker)) return;
        try
        {
            var sql = $"SELECT date, adj_close FROM daily_prices " +
                      $"WHERE ticker='{ticker}' ORDER BY date ASC";
            _allData = QueryPrices(sql);
            if (_allData.Count == 0)
            {
                TxtStatus.Text = $"데이터 없음: {ticker}";
                StatusChanged?.Invoke($"데이터 없음: {ticker}", "#F9E2AF");
                return;
            }
            ApplyPeriod();
            StatusChanged?.Invoke($"{ticker}  {_allData.Count:N0}일  " +
                                  $"{_allData.First().date:yyyy-MM-dd} ~ {_allData.Last().date:yyyy-MM-dd}",
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

        var points = data
            .Select(d => new DateTimePoint(d.date, d.close))
            .ToList();

        Series.Clear();
        Series.Add(new LineSeries<DateTimePoint>
        {
            Values      = new ObservableCollection<DateTimePoint>(points),
            Fill        = new SolidColorPaint(SKColor.Parse("#89B4FA").WithAlpha(25)),
            Stroke      = new SolidColorPaint(SKColor.Parse("#89B4FA")) { StrokeThickness = 2 },
            GeometrySize    = 0,
            GeometryFill    = null,
            GeometryStroke  = null,
            LineSmoothness  = 0,
            Name        = TxtTicker.Text.Trim().ToUpper(),
        });

        // 최고/최저 표시
        var hi  = data.MaxBy(d => d.close);
        var lo  = data.MinBy(d => d.close);
        var ret = (data.Last().close / data.First().close - 1) * 100;
        TxtInfo.Text = $"▲ {hi.close:N0}  ▼ {lo.close:N0}   수익률 {ret:+0.00;-0.00}%";
    }

    /// <summary>LeftSidePanel에서 종목 선택 시 외부에서 호출</summary>
    public void LoadTicker(string ticker)
    {
        TxtTicker.Text = ticker.ToUpper();
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
        // 활성 버튼 색상
        foreach (var b in new[] { Btn1M, Btn3M, Btn6M, Btn1Y, Btn2Y, BtnAll })
            b.Foreground = System.Windows.Media.Brushes.Gray;
        btn.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#89B4FA"));
        ApplyPeriod();
    }

    private List<(DateTime, double)> QueryPrices(string sql)
    {
        var result = new List<(DateTime, double)>();
        using var conn = new DuckDBConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            var dateStr = reader.GetValue(0)?.ToString() ?? "";
            if (!DateTime.TryParse(dateStr, out var dt)) continue;
            if (!double.TryParse(reader.GetValue(1)?.ToString(), out var price)) continue;
            result.Add((dt, price));
        }
        return result;
    }
}
