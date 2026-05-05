using DuckDB.NET.Data;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Quant.UI.Views;

public partial class EditGroupView : UserControl
{
    private static readonly string DbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "quant", "quant.duckdb");

    public event Action<string, string>? StatusChanged;

    private DataTable? _table;

    public EditGroupView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadData();
    }

    public void LoadData()
    {
        try
        {
            _table = Query(
                "SELECT group_id, kind, name, description, rating, is_active, created_at, updated_at " +
                "FROM groups ORDER BY kind, name");
            Grid.ItemsSource = _table.DefaultView;
            TxtRowCount.Text = $"{_table.Rows.Count:N0} rows";
            StatusChanged?.Invoke($"그룹 {_table.Rows.Count}건", "#A6E3A1");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"오류: {ex.Message}", "#F38BA8");
        }
    }

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new GroupEditDialog(DbPath, null);
        if (dlg.ShowDialog() == true) LoadData();
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not DataRowView row)
        {
            TxtStatus.Text = "행을 선택하세요";
            return;
        }
        var dlg = new GroupEditDialog(DbPath, row);
        if (dlg.ShowDialog() == true) LoadData();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not DataRowView row)
        {
            TxtStatus.Text = "행을 선택하세요";
            return;
        }
        var name = row["name"]?.ToString() ?? "";
        var id   = row["group_id"]?.ToString() ?? "";
        var res  = MessageBox.Show($"그룹 [{name}]을 삭제하시겠습니까?",
                                   "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;
        try
        {
            Execute($"DELETE FROM groups WHERE group_id = {id}");
            StatusChanged?.Invoke($"삭제됨: {name}", "#F38BA8");
            LoadData();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"삭제 오류: {ex.Message}", "#F38BA8");
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

    private void Execute(string sql)
    {
        using var conn = new DuckDBConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
