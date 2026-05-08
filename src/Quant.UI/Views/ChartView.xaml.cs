using DuckDB.NET.Data;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Quant.Core.Infrastructure;
using Quant.Core.Services;
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
    private string _currentTicker = "";

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
            Labeler         = v =>
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
        _currentTicker = ticker;
        try
        {
            // rating 로드
            using (var conn = _db.OpenNativeConnection())
            using (var cmd  = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT rating FROM stocks WHERE ticker = $1";
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
                var result = cmd.ExecuteScalar();
                RatingCtrl.Rating = (result is not null && result is not DBNull)
                    ? Math.Clamp(Convert.ToInt32(result), 0, 10) : 0;
            }
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

	private async void BtnDownloadData_Click(object sender, RoutedEventArgs e)
    {
        var ticker = TxtTicker.Text.Trim().ToUpper();
        if (string.IsNullOrEmpty(ticker)) return;

        // 버튼 비활성화 (중복 클릭 방지)
        if (sender is Button btn) btn.IsEnabled = false;

        try
        {
            var svc      = new PriceDownloadService(_db);
            var progress = new Progress<string>(msg =>
                StatusChanged?.Invoke(msg, "#89B4FA"));

            var (inserted, lastDate) = await svc.DownloadAsync(ticker, progress);

            // 차트 리로드
            LoadChart();

            var msg = inserted > 0
                ? $"{ticker}: {inserted:N0}건 저장 완료 (최신 {lastDate})"
                : $"{ticker}: 신규 데이터 없음 (최신 {lastDate})";
            StatusChanged?.Invoke(msg, "#A6E3A1");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"다운로드 오류: {ex.Message}", "#F38BA8");
        }
        finally
        {
            if (sender is Button btn2) btn2.IsEnabled = true;
        }
	}

    private void RatingCtrl_RatingChanged(int rating)
    {
        if (string.IsNullOrEmpty(_currentTicker)) return;
        try
        {
            using var conn = _db.OpenNativeConnection();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = "UPDATE stocks SET rating=$1, updated_at=CURRENT_TIMESTAMP WHERE ticker=$2";
            cmd.Parameters.Add(new DuckDBParameter { Value = rating         });
            cmd.Parameters.Add(new DuckDBParameter { Value = _currentTicker });
            cmd.ExecuteNonQuery();
            StatusChanged?.Invoke($"{_currentTicker} rating 저장: {rating}", "#A6E3A1");
        }
        catch (Exception ex) { StatusChanged?.Invoke($"rating 저장 오류: {ex.Message}", "#F38BA8"); }
    }
	
}
