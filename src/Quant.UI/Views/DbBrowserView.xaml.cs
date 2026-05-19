using Quant.Core.Infrastructure;
using System.Data;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.RegularExpressions;

namespace Quant.UI.Views;

public partial class DbBrowserView : UserControl
{
    private static readonly string[] KnownTables =
    [
        "stocks", "groups", "stock_group_map",
        "fundamentals", "daily_prices", "stock_cache", "supply",
        "watchlists", "watchlist_items", "pdf_reports",
        "data_update_log", "trading_calendar", "options",
        "v_sectors", "v_themes", "v_active_group_map", "v_stock_primary_sector"
    ];

    public event Action<string, string>? StatusChanged;
    public event Action<string>? ElapsedChanged;

    private readonly DbManager _db;

    public DbBrowserView(DbManager db)
    {
        _db = db;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildTableButtons();
        CheckDbConnection();

        var ticker = DbManager.Instance.LoadOptions().LastTicker;
        TxtCurrTicker.Text = $"{_db.GetStockName(ticker)}:{ticker}";
        TxtWhere.Text = !string.IsNullOrWhiteSpace(ticker) ? $"ticker = '{ticker}'" : "1";
    }
     
    private void BuildTableButtons()
    {
        TableList.Children.Clear();
        foreach (var tbl in KnownTables)
        {
            var btn = new Button
            {
                Content = tbl,
                //Style   = (Style)Resources["TableButton"],
                Tag     = tbl
            };
            btn.Click += TableBtn_Click;
            TableList.Children.Add(btn);
        }
    }

    private string? _currentTable; // 테이블 목록에서 클릭 시 저장되는 변수라고 가정
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

        _currentTable = btn.Tag?.ToString();
        BuildSqlSample(_currentTable, "SELECT");
        //TxtSql.Text     = $"SELECT * FROM {_currentTable} WHERE 1 LIMIT 500";
        RunQuery(TxtSql.Text);

        TxtSqlSample.Text = $@"
SELECT c.id,COUNT(*)
FROM {_currentTable} p
JOIN cat c ON p.id = c.id
WHERE p.price >= 100
GROUP BY c.id
HAVING COUNT(*) >= 5
ORDER BY COUNT(*) DESC
LIMIT 10;

UPDATE {_currentTable}
SET name = 'Alice', age  = 27
WHERE id = 1;

INSERT INTO {_currentTable} (id, name, age)
VALUES (2, 'Bob', 30),(3, 'Charlie', 28);

DELETE FROM {_currentTable} WHERE id = 1;

WHERE age BETWEEN 20 AND 30
WHERE category IN ('A', 'B', 'C')
WHERE name LIKE 'Kim%'
WHERE deleted_at IS NULL";
    }

    // DbBrowserView.xaml.cs 내부에 추가할 코드 예시

    private void CmbCommandType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BuildSqlSample(_currentTable,
            (CmbCommandType.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "SELECT");
    }
    private void BuildSqlSample(string? table, string commandType)
    {
        if (TxtSql == null) return; // 초기 로드 시 null 참조 방지
        if (string.IsNullOrWhiteSpace(table)) table = "[table]";

        string where = TxtWhere?.Text ?? "1";

        TxtSql.Text = commandType switch
        {
            "SELECT" => $"SELECT * FROM {table} WHERE {where} LIMIT 500",
            "UPDATE" => $"UPDATE {table} SET col = val WHERE {where}",
            "INSERT" => $"INSERT INTO {table} (col1, col2) VALUES (val1, val2)",
            "DELETE" => $"DELETE FROM {table} WHERE {where}",
            _ => TxtSql.Text
        };
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

    private void BtnFunction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string table) return;

        var ticker = DbManager.Instance.LoadOptions().LastTicker;
        if (string.IsNullOrWhiteSpace(ticker) || !Helpers.TickerRegex.IsMatch(ticker)) return;

        string sql = $"DELETE FROM {table}";
        if (table != "stock_cache")
            sql = $"{sql} WHERE ticker = '{ticker}'";

        TxtSql.Text = sql; // 생성된 쿼리를 텍스트박스에 표시

        // if (MessageBox.Show($"[{table}] 테이블에서 데이터를 삭제하시겠습니까?\n\n쿼리: {sql}", 
        //     "데이터 삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        // {
        //     RunQuery(sql);
        // }
    }
}
