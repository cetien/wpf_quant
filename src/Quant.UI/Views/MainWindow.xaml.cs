using DuckDB.NET.Data;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Quant.UI.Views;

public partial class MainWindow : Window
{
    //private static bool _duckDbNativeReady;

    private static readonly string DbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "quant", "quant.duckdb");

    private static readonly string[] KnownTables =
    [
        "stocks", "groups", "stock_group_map",
        "fundamentals", "daily_prices", "supply", 
        "watchlists", "watchlist_items", "pdf_reports",
        "data_update_log", "trading_calendar"
    ];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TxtDbPath.Text = ShortenPath(DbPath);
        BuildTableButtons();
        CheckDbConnection();
    }

    private void BuildTableButtons()
    {
        TableList.Children.Clear();
        foreach (var tbl in KnownTables)
        {
            var btn = new Button { Content = tbl, Style = (Style)Resources["TableButton"], Tag = tbl };
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
        btn.Background = MakeBrush("#313244");
        btn.Foreground = MakeBrush("#89B4FA");
        TxtSql.Text = $"SELECT * FROM {btn.Tag} LIMIT 500";
        RunQuery(TxtSql.Text);
    }

    private void BtnRun_Click(object sender, RoutedEventArgs e) => RunQuery(TxtSql.Text);

    private void TxtSql_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.Control)
            RunQuery(TxtSql.Text);
    }

    private void RunQuery(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;
        var sw = Stopwatch.StartNew();
        ShowMsg("실행 중...", "#89B4FA");
        TxtRowCount.Text = "";
        try
        {
            if (!File.Exists(DbPath))
            {
                MainGrid.ItemsSource = null;
                ShowMsg($"DB 없음: {DbPath}", "#F38BA8");
                return;
            }
            var dt = FetchDataTable(sql);
            sw.Stop();
            MainGrid.ItemsSource = dt.DefaultView;
            TxtRowCount.Text = $"{dt.Rows.Count:N0} rows  |  {dt.Columns.Count} cols";
            ShowMsg("완료", "#A6E3A1");
            TxtElapsed.Text = $"{sw.ElapsedMilliseconds} ms";
        }
        catch (Exception ex)
        {
            sw.Stop();
            MainGrid.ItemsSource = null;
            ShowMsg($"오류: {ex.Message}", "#F38BA8");
            TxtElapsed.Text = $"{sw.ElapsedMilliseconds} ms";
        }
    }

    private DataTable FetchDataTable(string sql)
    {
        //EnsureDuckDbNativeLoaded();
        var dt = new DataTable();
        using var conn = new DuckDBConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        for (int i = 0; i < reader.FieldCount; i++)
            dt.Columns.Add(reader.GetName(i), typeof(string));
        while (reader.Read())
        {
            var row = dt.NewRow();
            for (int i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "";
            dt.Rows.Add(row);
        }
        return dt;
    }

    //private static void EnsureDuckDbNativeLoaded()
    //{
    //    if (_duckDbNativeReady) return;

    //    var baseDir = AppContext.BaseDirectory;
    //    var candidates = new[]
    //    {
    //        Path.Combine(baseDir, "duckdb.dll"),
    //        Path.Combine(baseDir, "runtimes", "win-x64", "native", "duckdb.dll"),
    //        Path.Combine(baseDir, "runtimes", "win-x86", "native", "duckdb.dll")
    //    };

    //    foreach (var path in candidates)
    //    {
    //        if (!File.Exists(path)) continue;
    //        if (NativeLibrary.TryLoad(path, out _))
    //        {
    //            _duckDbNativeReady = true;
    //            return;
    //        }
    //    }

    //    throw new DllNotFoundException(
    //        $"duckdb.dll not found. Checked: {string.Join(", ", candidates)}");
    //}

    private void CheckDbConnection()
    {
        if (!File.Exists(DbPath))
        {
            TxtStatus.Text = "DB 없음";
            ShowMsg($"DB 파일 없음: {DbPath}", "#F9E2AF");
            return;
        }
        try
        {
            var dt = FetchDataTable(
                "SELECT table_name FROM information_schema.tables WHERE table_schema='main' ORDER BY table_name");
            var tables = dt.Rows.Cast<DataRow>().Select(r => r[0]?.ToString() ?? "").ToList();
            TxtStatus.Text = $"  DB 연결됨  |  테이블 {tables.Count}개";
            ShowMsg("DB 연결 성공. 테이블 선택 또는 Ctrl+Enter로 SQL 실행", "#A6E3A1");
            foreach (Button btn in TableList.Children)
                btn.Opacity = tables.Contains(btn.Tag?.ToString() ?? "") ? 1.0 : 0.35;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "DB 연결 실패";
            ShowMsg($"오류: {ex.Message}", "#F38BA8");
        }
    }

    private void ShowMsg(string msg, string hex)
    {
        TxtMsg.Text = msg;
        TxtMsg.Foreground = MakeBrush(hex);
    }

    private static SolidColorBrush MakeBrush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;

    private static string ShortenPath(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return path.StartsWith(local, StringComparison.OrdinalIgnoreCase)
            ? "%LOCALAPPDATA%" + path[local.Length..]
            : path;
    }
}
