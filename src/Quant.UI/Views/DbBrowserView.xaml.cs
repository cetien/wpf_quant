using Quant.Core.Infrastructure;
using Quant.UI.Common;

using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Quant.UI.Views;

public partial class DbBrowserView : UserControl, ITickerNavigationAware
{
    private static readonly string[] KnownTables =
    [
        "stocks", "groups", "stock_group_map",
        "fundamentals", "daily_prices", "stock_cache", "supply",
        "watchlists", "watchlist_items", "pdf_reports",
        "data_update_log", "trading_calendar", "options",
        "stock_tp", "target_price_stocks", "target_price_monthly",
        "v_sectors", "v_themes", "v_active_group_map", "v_stock_primary_group"
    ];

    private readonly IMainActions _main;
    //public event Action<string, string>? StatusChanged;
    public event Action<string>? ElapsedChanged;

    private readonly DbManager _db;

    public DbBrowserView(IMainActions main)
    {
        _main = main;
        _db = App.DB;// main.Db;
        //_db = db;

        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildTableButtons();
    }

    public void OnNavigatedToTicker(string id)
    {
        CheckDbConnection();
        BuildTableButtons();

        //var ticker = App.Config().LastTicker;
        TxtCurrTicker.Text = $"{_db.StockName(id)}:{id}";
        TxtWhere.Text = !string.IsNullOrWhiteSpace(id) ? $"ticker = '{id}'" : "1";

        //_currentTable = "daily_prices";
        BuildSqlSample(_currentTable, "SELECT");
        RunQuery(TxtSql.Text);
    }

    private void BuildTableButtons()
    {
        IEnumerable<string> tables = KnownTables;
        if (_db.IsConnected())
        {
            try
            {
                var dt = _db.Query(
                    "SELECT table_name FROM information_schema.tables WHERE table_schema='main' ORDER BY table_type, table_name");
                var dbTables = dt.Rows.Cast<DataRow>()
                                 .Select(r => r[0]?.ToString() ?? "")
                                 .Where(t => !string.IsNullOrEmpty(t))
                                 .ToList();
                if (dbTables.Count > 0) tables = dbTables;
            }
            catch { /* fallback to KnownTables */ }
        }

        TableList.Children.Clear();
        foreach (var tbl in tables)
        {
            var btn = new Button { Content = tbl, Tag = tbl };
            btn.Click += TableBtn_Click;
            TableList.Children.Add(btn);
        }
    }

    private string? _currentTable = "daily_prices"; // 테이블 목록에서 클릭 시 저장
    private void TableBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        foreach (Button b in TableList.Children)
        {
            b.Background = Brushes.Transparent;
            b.Foreground = MakeBrush("#CDD6F4");
        }
        btn.Background = MakeBrush("#313244");
        btn.Foreground = MakeBrush("#89B4FA");

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


    private void CmbSqlOption_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        BuildSqlSample(_currentTable, (CmbCommandType?.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "SELECT");

    private void BuildSqlSample(string? table, string commandType)
    {
        if (TxtSql == null) return;
        if (string.IsNullOrWhiteSpace(table)) table = "[table]";

        string where = TxtWhere?.Text ?? "1";

        if (commandType == "SELECT")
        {
            string joinVal = (CmbJoin?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            string groupVal = (CmbGroupBy?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            string orderVal = (CmbOrderBy?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

            bool hasJoin = !string.IsNullOrEmpty(joinVal);
            string from = hasJoin ? $"{table} s" : table;

            string sql = $"SELECT * FROM {from}";
            if (hasJoin) sql += $" {joinVal}";
            sql += $" WHERE {where}";
            if (!string.IsNullOrEmpty(groupVal)) sql += $" GROUP BY {groupVal}";
            if (!string.IsNullOrEmpty(orderVal)) sql += $" ORDER BY {orderVal}";
            sql += " LIMIT 500";

            TxtSql.Text = sql;
        }
        else
        {
            TxtSql.Text = commandType switch
            {
                "UPDATE" => $"UPDATE {table} SET col = val WHERE {where}",
                "INSERT" => $"INSERT INTO {table} (col1, col2) VALUES (val1, val2)",
                "DELETE" => $"DELETE FROM {table} WHERE {where}",
                _ => TxtSql.Text
            };
        }
    }


    private string ActiveSql => TxtSql.SelectionLength > 0 ? TxtSql.SelectedText : TxtSql.Text;

    internal void BtnRun_Click(object sender, RoutedEventArgs e) => RunQuery(ActiveSql);

    internal void TxtSql_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.Control)
            RunQuery(ActiveSql);
    }

    private void RunQuery(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;
        var sw = Stopwatch.StartNew();

        _main.StatusInfo("실행 중...");
        TxtRowCount.Text = "";
        try
        {
            if (!_db.IsConnected())
            {
                MainGrid.ItemsSource = null;
                _main.StatusError($"DB 없음: {DbManager.DbPath}");
                return;
            }
            var dt = _db.Query(sql);
            sw.Stop();
            MainGrid.ItemsSource = dt.DefaultView;
            TxtRowCount.Text = $"{dt.Rows.Count:N0} rows  |  {dt.Columns.Count} cols";
            _main.StatusSuccess("완료");
            ElapsedChanged?.Invoke($"{sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            sw.Stop();
            MainGrid.ItemsSource = null;
            _main.StatusError($"오류: {ex.Message}");
            ElapsedChanged?.Invoke($"{sw.ElapsedMilliseconds} ms");
        }
    }

    private void CheckDbConnection()
    {
        if (!_db.IsConnected())
        {
            _main.StatusError($"DB 파일 없음: {DbManager.DbPath}");
            return;
        }
        try
        {
            var dt = _db.Query(
                "SELECT table_name FROM information_schema.tables WHERE table_schema='main' ORDER BY table_name");
            var tables = dt.Rows.Cast<DataRow>().Select(r => r[0]?.ToString() ?? "").ToList();
            _main.StatusInfo($"DB 연결됨  |  테이블 {tables.Count}개 — 테이블 선택 또는 Ctrl+Enter로 SQL 실행");
            foreach (Button btn in TableList.Children)
                btn.Opacity = tables.Contains(btn.Tag?.ToString() ?? "") ? 1.0 : 0.35;
        }
        catch (Exception ex)
        {
            _main.StatusException(ex, "DB 연결 실패");
        }
    }

    private static SolidColorBrush MakeBrush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;

    private void BtnFunction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string table) return;

        string sql = $"DELETE FROM {table}";
        if (table != "stock_cache")
        {
            var ticker = App.Config().LastTicker;
            if (string.IsNullOrWhiteSpace(ticker) || !Helpers.TickerRegex.IsMatch(ticker)) return;
            sql += $" WHERE ticker = '{ticker}'";
        }

        TxtSql.Text = sql; // 생성된 쿼리를 텍스트박스에 표시

        // if (MessageBox.Show($"[{table}] 테이블에서 데이터를 삭제하시겠습니까?\n\n쿼리: {sql}", 
        //     "데이터 삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        // {
        //     RunQuery(sql);
        // }
    }

    private void BtnCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string command) return;
        if (string.IsNullOrWhiteSpace(_currentTable)) return;

        var dtInfo = _db.Query($"PRAGMA table_info({_currentTable})");
        if (dtInfo == null || dtInfo.Rows.Count == 0) return;

        string pk = dtInfo.AsEnumerable()
                      .Where(r => Convert.ToInt32(r["pk"]) > 0)
                      .Select(r => r["name"].ToString())
                      .FirstOrDefault() ?? "id";

        var dtColumns = _db.Query($@"
            SELECT column_name
            FROM information_schema.columns
            WHERE table_name = '{_currentTable}'
            ORDER BY ordinal_position");
        var cols = dtColumns.AsEnumerable()
                     .Select(r => r.Field<string>("column_name") ?? "")
                     .ToList();

        string columns = string.Join(", ", cols);
        string col2    = cols.Count > 1 ? cols[1] : cols[0];

        TxtSql.Text = command switch
        {
            "UPDATE" => $"UPDATE {_currentTable} SET {col2} = '' WHERE {pk} = '0'\n -- {columns}",
            "INSERT" => $"INSERT INTO {_currentTable} ({pk}, {col2}) VALUES ('val1', 'val2')\n -- {columns}",
            "DELETE" => $"DELETE FROM {_currentTable} WHERE {pk} = '0'\n -- {columns}",
            _ => TxtSql.Text
        };
    }
}
