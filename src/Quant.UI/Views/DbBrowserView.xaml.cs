using Quant.Core.Infrastructure;
using System.Data;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Quant.UI.Views;

public partial class DbBrowserView : UserControl
{
    private static readonly string[] KnownTables =
    [
        "stocks", "groups", "stock_group_map",
        "fundamentals", "daily_prices", "supply",
        "watchlists", "watchlist_items", "pdf_reports",
        "data_update_log", "trading_calendar", "options"
	];

    public event Action<string, string>? StatusChanged;
    public event Action<string>? ElapsedChanged;

    private readonly DbManager _db = DbManager.Instance;

    public DbBrowserView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildTableButtons();
        CheckDbConnection();
    }

    private void BuildTableButtons()
    {
        TableList.Children.Clear();
        foreach (var tbl in KnownTables)
        {
            var btn = new Button
            {
                Content = tbl,
                Style   = (Style)Resources["TableButton"],
                Tag     = tbl
            };
            btn.Click += TableBtn_Click;
            TableList.Children.Add(btn);
        }
    }

    private void TableBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        foreach (Button b in TableList.Children)
        {
            b.Background = Brushes.Transparent;
            b.Foreground = MakeBrush("#CDD6F4");
        }
        btn.Background  = MakeBrush("#313244");
        btn.Foreground  = MakeBrush("#89B4FA");
        TxtSql.Text     = $"SELECT * FROM {btn.Tag} LIMIT 500";
        RunQuery(TxtSql.Text);
    }

    internal void BtnRun_Click(object sender, RoutedEventArgs e) => RunQuery(TxtSql.Text);

    internal void TxtSql_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.Control)
            RunQuery(TxtSql.Text);
    }

    private void RunQuery(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;
        var sw = Stopwatch.StartNew();
        StatusChanged?.Invoke("실행 중...", "#89B4FA");
        TxtRowCount.Text = "";
        try
        {
            if (!_db.IsConnected())
            {
                MainGrid.ItemsSource = null;
                StatusChanged?.Invoke($"DB 없음: {DbManager.DbPath}", "#F38BA8");
                return;
            }
            var dt = _db.Query(sql);
            sw.Stop();
            MainGrid.ItemsSource = dt.DefaultView;
            TxtRowCount.Text = $"{dt.Rows.Count:N0} rows  |  {dt.Columns.Count} cols";
            StatusChanged?.Invoke("완료", "#A6E3A1");
            ElapsedChanged?.Invoke($"{sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            sw.Stop();
            MainGrid.ItemsSource = null;
            StatusChanged?.Invoke($"오류: {ex.Message}", "#F38BA8");
            ElapsedChanged?.Invoke($"{sw.ElapsedMilliseconds} ms");
        }
    }

    private void CheckDbConnection()
    {
        if (!_db.IsConnected())
        {
            StatusChanged?.Invoke($"DB 파일 없음: {DbManager.DbPath}", "#F9E2AF");
            return;
        }
        try
        {
            var dt = _db.Query(
                "SELECT table_name FROM information_schema.tables WHERE table_schema='main' ORDER BY table_name");
            var tables = dt.Rows.Cast<DataRow>().Select(r => r[0]?.ToString() ?? "").ToList();
            StatusChanged?.Invoke($"DB 연결됨  |  테이블 {tables.Count}개 — 테이블 선택 또는 Ctrl+Enter로 SQL 실행", "#A6E3A1");
            foreach (Button btn in TableList.Children)
                btn.Opacity = tables.Contains(btn.Tag?.ToString() ?? "") ? 1.0 : 0.35;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"DB 연결 실패: {ex.Message}", "#F38BA8");
        }
    }

    private static SolidColorBrush MakeBrush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
}
