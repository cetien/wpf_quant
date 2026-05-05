using DuckDB.NET.Data;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Quant.UI.Views;

public partial class ReportView : UserControl
{
    private static readonly string DbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "quant", "quant.duckdb");

    public event Action<string, string>? StatusChanged;

    private DataTable? _table;

    public ReportView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadData();
    }

    private void LoadData()
    {
        try
        {
            _table = Query(
                "SELECT id, date, ticker, title, writer, filepath, created_at " +
                "FROM pdf_reports ORDER BY date DESC, created_at DESC LIMIT 2000");
            Grid.ItemsSource = _table.DefaultView;
            TxtRowCount.Text = $"{_table.Rows.Count:N0} rows";
            StatusChanged?.Invoke($"리포트 {_table.Rows.Count:N0}건 로드됨", "#A6E3A1");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"오류: {ex.Message}", "#F38BA8");
        }
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is not DataRowView row) return;
        var filepath = row["filepath"]?.ToString();
        if (string.IsNullOrWhiteSpace(filepath))
        {
            TxtStatus.Text = "파일 경로가 없습니다";
            return;
        }
        if (!File.Exists(filepath))
        {
            TxtStatus.Text = $"파일 없음: {filepath}";
            return;
        }
        OpenWithChrome(filepath);
    }

    private void OpenWithChrome(string path)
    {
        // Chrome 위치 후보
        string[] chromeCandidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
        ];

        var chrome = chromeCandidates.FirstOrDefault(File.Exists);
        try
        {
            if (chrome is not null)
                Process.Start(chrome, $"\"{path}\"");
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

            TxtStatus.Text = $"열기: {Path.GetFileName(path)}";
            StatusChanged?.Invoke($"열기: {Path.GetFileName(path)}", "#89B4FA");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"오류: {ex.Message}";
            StatusChanged?.Invoke($"오류: {ex.Message}", "#F38BA8");
        }
    }

    private DataTable Query(string sql)
    {
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
                row[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "";
            dt.Rows.Add(row);
        }
        return dt;
    }
}
